using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Contract;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// The one implementation of IMemoryService: the single door to the memory store.
// Search runs behind the PetBox.Core.Search contract (SearchService facade over a Class-A
// SqliteFtsIndex + a Class-B VectorSearchIndex): the lexical floor is written INSIDE the entity
// transaction (never lexically-stale), and vectors are materialized off the write path by the
// async-vectorization worker (a down embedder never blocks or loses a write). Design: m-b3fbe908.
public sealed class MemoryService : IMemoryService
{
	// MRL truncation dim for the vector index — 1024 is the LoCoMo sweet spot for the 2560-d
	// embedder (m-981885fb/m-ea6236b1); 256 over-truncates, 2560 adds nothing.
	const int VectorDim = 1024;
	// Fusion candidate depth; type filtering happens after resolution, so keep it generous.
	const int SearchK = 50;

	// The agent memory canon (spec agent-wiring, memory-canon-storage): store "canon", one
	// `index` entry per scope, pulled into every session's context by the wiring hooks. It MUST
	// stay a compact index of pointers, so its entry bodies carry a hard budget — enforced in the
	// service door (UpsertAsync) so BOTH memory_upsert and memory_remember are covered.
	const string CanonStore = "canon";
	const int CanonBodyBudget = 10000;

	readonly IMemoryStore _stores;
	// Optional embedding capability (DI auto-fills when an LLM router is registered).
	// Null → semantic search disabled (lexical-only); never throws.
	readonly ILlmClient? _llm;
	// Relevance re-ranking policy (freshness decay + MMR diversity) bound from the `Search`
	// config section. Defaults are enabled/conservative, so an un-wired construction (tests,
	// other adapters) still gets the shipped ranking.
	readonly SearchRerankOptions _rerank;

	public MemoryService(IMemoryStore stores, ILlmClient? llm = null, SearchRerankOptions? rerank = null)
	{
		_stores = stores;
		_llm = llm;
		_rerank = rerank ?? new SearchRerankOptions();
	}

	// ---- store lifecycle ----

	public Task<MemoryStoreMeta> CreateStoreAsync(string projectKey, string store, string? description, CancellationToken ct = default) =>
		_stores.CreateAsync(projectKey, store, description, ct);

	public Task<IReadOnlyList<MemoryStoreMeta>> ListStoresAsync(string projectKey, CancellationToken ct = default) =>
		_stores.ListAsync(projectKey, ct);

	public Task<bool> DeleteStoreAsync(string projectKey, string store, CancellationToken ct = default) =>
		_stores.DeleteAsync(projectKey, store, ct);

	public Task<bool> StoreExistsAsync(string projectKey, string store, CancellationToken ct = default) =>
		_stores.ExistsAsync(projectKey, store, ct);

	// ---- entries ----

	public async Task<IReadOnlyList<MemoryEntryView>> ListAsync(string projectKey, string store, string? type, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var ctx = _stores.GetContext(projectKey, store);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);
		return ListActive(ctx, typeFilter).Select(View).ToList();
	}

	public async Task<MemoryEntryView?> GetAsync(string projectKey, string store, string key, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var ctx = _stores.GetContext(projectKey, store);
		var e = ctx.Entries.Where(x => x.Key == key && x.ActiveTo == null).ToList().FirstOrDefault();
		return e is null ? null : View(e);
	}

	public async Task<MemorySearchResult> SearchAsync(string projectKey, string store, string query, string? type, bool? lexical = null, bool? semantic = null, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);
		var (hits, retrievers) = await SearchStoreAsync(projectKey, store, query, typeFilter, SearchK, lexical, semantic, ct);
		return new MemorySearchResult(hits.Select(h => View(h.Entry)).ToList(), retrievers);
	}

	public async Task<MemoryScoredSearchResult> SearchScoredAsync(string projectKey, string store, string query, string? type, bool? lexical = null, bool? semantic = null, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);
		var (hits, retrievers) = await SearchStoreAsync(projectKey, store, query, typeFilter, SearchK, lexical, semantic, ct);
		// Load the pool's vectors ONCE (when an embedder is wired) so a caller's MMR has proximity;
		// without an embedder there is nothing to load and MMR degrades to identity downstream.
		var vecs = _llm is null ? null : LoadVectors(projectKey, store, hits.Select(h => h.Entry.Key).ToList());
		var scored = hits.Select(h => new MemoryScoredHit(
			View(h.Entry), h.Entry.Updated, h.Score, h.LexicalConfirmed,
			vecs is not null && vecs.TryGetValue(h.Entry.Key, out var v) ? v : null)).ToList();
		return new MemoryScoredSearchResult(scored, retrievers);
	}

	// ---- unified read (list = search without a query; uniform-entity-verbs v2) ----

	// Stores skipped by the implicit "every store" sweep of the unified read. DECOUPLED from
	// IsSystem on purpose (card ui-memory-system-store-widen): sweep-exclusion is a NARROW recall
	// policy, not the system-store badge. Members: "ops" (sensitive operational — has held secrets,
	// must never auto-pull into an agent's context) and "session-digests" (the always-on digest
	// index the digest job maintains — a summary layer that would double-count against the raw
	// stores in default recall). Protected system stores that ARE knowledge — canon, autocaptured —
	// stay in the sweep, so default memory_search still returns them. An explicit Filter.Store
	// still reaches any store; only the implicit sweep excludes (spec: memoverhaul store taxonomy).
	static readonly HashSet<string> SweepExcludedStores =
		new(StringComparer.OrdinalIgnoreCase) { "ops", "session-digests" };

	// The generic uniform-read seam (implemented EXPLICITLY — the contract is a shared shape,
	// not a DI dispatch point): the plain envelope of the rich SearchEntriesAsync. Budget
	// markers stay null here — the response budget is measured on the WIRE rows, so it
	// belongs to the adapter that shapes them.
	async Task<SearchEnvelope<MemoryEntryHit>> ISearchService<MemoryEntryHit, MemoryEntryFilter, MemorySortBy>.SearchAsync(
		string projectKey, SearchRequest<MemoryEntryFilter, MemorySortBy> request, CancellationToken ct)
	{
		var r = await SearchEntriesAsync(projectKey, request, ct);
		return new SearchEnvelope<MemoryEntryHit>(r.Hits, Retrievers: r.Retrievers);
	}

	public async Task<MemoryEntrySearchResult> SearchEntriesAsync(string projectKey, SearchRequest<MemoryEntryFilter, MemorySortBy> request, CancellationToken ct = default)
	{
		var f = request.Filter ?? new MemoryEntryFilter();
		var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
		if (query is null && request.Sort is { By: MemorySortBy.Relevance })
			throw new ArgumentException("sort by relevance needs a query (q) — without one the read is a deterministic listing (default order: updated desc)");
		var typeFilter = string.IsNullOrWhiteSpace(f.Type) ? (MemoryType?)null : ParseType(f.Type!);

		// Stores in scope: the named one, or the implicit sweep (skips sensitive stores).
		// A named store missing from THIS container yields no hits, not an error — the
		// adapter's scope cascade reads several containers and the store may live in one.
		IReadOnlyList<string> stores = string.IsNullOrWhiteSpace(f.Store)
			? (await _stores.ListAsync(projectKey, ct)).Where(s => !SweepExcludedStores.Contains(s.Name)).Select(s => s.Name).ToList()
			: [f.Store!.Trim()];

		// Each candidate carries its owning store, entry, the fused relevance Score (query mode),
		// and the raw per-store vector (for MMR; null in listing mode / no embedder).
		var selected = new List<Candidate>();
		SearchRetrievers? retrievers = null;
		foreach (var store in stores)
		{
			ct.ThrowIfCancellationRequested();
			if (!await _stores.ExistsAsync(projectKey, store, ct)) continue;
			if (query is null)
			{
				// LISTING: the active entries of the store (deterministic; ordered below).
				selected.AddRange(ListActive(_stores.GetContext(projectKey, store), typeFilter)
					.Select(e => new Candidate(store, e, 0, null)));
			}
			else
			{
				// QUERY: hybrid selection per store; the fused ranking supplies a bounded
				// CANDIDATE POOL of max(3×limit, 50) — the same formula as tasks_search (3×
				// leaves the post-fusion type predicate room to drop candidates and still
				// fill the limit; the 50 floor keeps recall sane for small/unbounded asks).
				var (hits, r) = await SearchStoreAsync(projectKey, store, query, typeFilter,
					Math.Max(request.Limit * 3, 50), lexical: null, semantic: null, ct);
				retrievers = retrievers is { } agg
					? new SearchRetrievers(agg.Lexical | r.Lexical, agg.Semantic | r.Semantic, agg.Degraded | r.Degraded)
					: r;
				var vecs = _llm is null ? null : LoadVectors(projectKey, store, hits.Select(h => h.Entry.Key).ToList());
				selected.AddRange(hits.Select(h => new Candidate(store, h.Entry, h.Score,
					vecs is not null && vecs.TryGetValue(h.Entry.Key, out var v) ? v : null, h.LexicalConfirmed)));
			}
		}
		if (query is not null) retrievers ??= new SearchRetrievers(false, false, false);

		// QUERY relevance order: fuse EVERY store's pool GLOBALLY by score (rank-based → comparable
		// across stores, so a strong hit in a late store outranks a weak one in an early store —
		// no longer a plain per-store concat), bleed in freshness (decay), then MMR-diversify.
		if (query is not null)
			selected = RankRelevance(selected);

		selected = SortSelected(selected, request.Sort, hasQuery: query is not null);
		if (request.Limit > 0 && selected.Count > request.Limit) selected = selected.Take(request.Limit).ToList();

		// Retriever provenance: query mode names how the hit surfaced (lexically confirmed vs
		// vector-only); a listing ran no relevance leg, so it stays null.
		var hits2 = selected.Select(x => new MemoryEntryHit(x.Store,
			request.BodyLen > 0 ? View(x.Entry) with { Body = SnippetBody(x.Entry.Body, request.BodyLen) } : View(x.Entry),
			x.Score,
			query is null ? null : (x.LexicalConfirmed ? "lexical" : "semantic"))).ToList();
		return new MemoryEntrySearchResult(hits2, retrievers);
	}

	// One selection candidate: its owning store, the entry, the fused relevance Score (query
	// mode; 0 in a listing), the entry's vector (for MMR; null without an embedder / in a
	// listing), and whether the lexical leg confirmed it (query mode — drives the semantic-noise
	// floor + retriever provenance; always false in a listing, where the floor never runs). A
	// record-struct so it slots into the existing list-building cheaply.
	readonly record struct Candidate(string Store, MemoryEntry Entry, double Score, float[]? Vector, bool LexicalConfirmed = false);

	// GLOBAL query relevance ordering across every store's candidate pool (spec memoverhaul):
	//   1. Freshness decay — multiply the fused RRF score by an exp half-life weight on Updated,
	//      so at comparable relevance the fresher fact wins (listing mode never reaches here).
	//   2. Global order by that blended score (a rank-based RRF score is comparable across
	//      stores → the single best hit wins regardless of container), ties → fresher, then key.
	//   3. MMR diversification so the head is not a wall of near-duplicates (vector proximity;
	//      silently identity without an embedder — no vectors were loaded).
	List<Candidate> RankRelevance(List<Candidate> candidates)
	{
		if (candidates.Count == 0) return candidates;

		// Semantic-noise floor (spec search-relevance-floor): FIRST drop a hit the lexical leg did
		// NOT confirm whose RAW fused score is below the floor — the floor operates on the raw RRF
		// score (decay only reorders survivors, it must not push a hit under the floor), mirroring
		// SessionSearchService. A lexically-confirmed hit is never floored; floor <= 0 disables.
		var floor = _rerank.Floor.SemanticFloor;
		if (floor > 0) candidates = candidates.Where(c => c.LexicalConfirmed || c.Score >= floor).ToList();

		var now = DateTime.UtcNow;
		var recency = _rerank.Recency;
		double Blended(Candidate c) => recency.Enabled
			? c.Score * RecencyDecay.Weight(c.Entry.Updated, now, recency.HalfLifeDays)
			: c.Score;

		var blended = candidates
			.Select(c => c with { Score = Blended(c) })
			.OrderByDescending(c => c.Score)
			.ThenByDescending(c => c.Entry.Updated)
			.ThenBy(c => c.Entry.Key, StringComparer.Ordinal)
			.ThenBy(c => c.Store, StringComparer.Ordinal)
			.ToList();

		var diversity = _rerank.Diversity;
		if (diversity.Enabled)
			blended = Mmr.Reorder(blended, c => c.Score, c => c.Vector, diversity.Lambda);
		return blended;
	}

	// Final ordering of the selected set. No sort: query mode keeps the fused RELEVANCE order
	// (global fusion + decay + MMR, already applied), a listing defaults to Updated desc (the
	// freshest fact first — keys are opaque generated ids, so key order carries no meaning). An
	// explicit created/updated sort reorders WITHIN the selected set; Relevance = keep the fused
	// order (guarded to query mode; `desc` is meaningless there and ignored). Ties break on key
	// then store for determinism.
	static List<Candidate> SortSelected(
		List<Candidate> hits, (MemorySortBy By, bool Desc)? sort, bool hasQuery)
	{
		if (sort is null)
			return hasQuery ? hits : Ordered(hits, x => x.Entry.Updated, desc: true);
		return sort.Value.By switch
		{
			MemorySortBy.Relevance => hits, // guarded to query mode; the fused order IS relevance
			MemorySortBy.Created => Ordered(hits, x => x.Entry.Created, sort.Value.Desc),
			MemorySortBy.Updated => Ordered(hits, x => x.Entry.Updated, sort.Value.Desc),
			_ => throw new ArgumentException($"unknown sort '{sort.Value.By}'"),
		};
	}

	static List<Candidate> Ordered<TKey>(
		List<Candidate> hits, Func<Candidate, TKey> key, bool desc) =>
		(desc ? hits.OrderByDescending(key) : hits.OrderBy(key))
			.ThenBy(x => x.Entry.Key, StringComparer.Ordinal)
			.ThenBy(x => x.Store, StringComparer.Ordinal)
			.ToList();

	// Load the stored vectors for the given entry keys from ONE store's search_vec (the Class-B
	// index table). Best-effort: a store that was never vectorized (no table/rows) simply yields
	// no vectors, so MMR falls back to identity for it. Only called when an embedder is wired.
	Dictionary<string, float[]> LoadVectors(string projectKey, string store, List<string> keys)
	{
		var map = new Dictionary<string, float[]>(StringComparer.Ordinal);
		if (keys.Count == 0) return map;
		try
		{
			var ctx = _stores.GetContext(projectKey, store);
			var rows = ctx.GetTable<VecRow>()
				.Where(r => r.Type == MemorySearchDocs.Type && keys.Contains(r.Id))
				.Select(r => new { r.Id, r.Vec })
				.ToList();
			foreach (var r in rows)
				map[r.Id] = VectorCodec.Decode(r.Vec);
		}
		catch
		{
			// No search_vec table yet (store never vectorized) — MMR degrades to identity, quietly.
		}
		return map;
	}

	// Minimal read-only mapping onto the vector index table owned by VectorSearchIndex; here only
	// to pull (Id, Vec) for MMR proximity, never written through this type.
	[LinqToDB.Mapping.Table("search_vec")]
	sealed class VecRow
	{
		[LinqToDB.Mapping.Column] public string Type { get; set; } = string.Empty;
		[LinqToDB.Mapping.Column] public string Id { get; set; } = string.Empty;
		[LinqToDB.Mapping.Column] public byte[] Vec { get; set; } = [];
	}

	// Hybrid selection over ONE store's open entries (lexical FTS5 ⊕ semantic vectors,
	// RRF-fused), entities returned in fused order WITH their fused RRF score (for global
	// cross-store fusion + decay upstream) and retriever provenance. `k` bounds the candidate
	// pool. The type filter applies post-resolution (Type is constant in the index, as before).
	async Task<(List<(MemoryEntry Entry, double Score, bool LexicalConfirmed)> Hits, SearchRetrievers Retrievers)> SearchStoreAsync(
		string projectKey, string store, string query, MemoryType? typeFilter, int k,
		bool? lexical, bool? semantic, CancellationToken ct)
	{
		var ctx = _stores.GetContext(projectKey, store);

		// No searchable tokens (empty/punctuation query): degrade to a type-filtered listing —
		// a filter-only query still returns a sensible set rather than nothing (preserved from
		// the pre-contract behaviour; FtsQuery returns no match for such queries). Score by
		// descending position so the listing order survives if decay is off. A deterministic
		// listing is not semantic noise, so every row counts as lexically confirmed.
		if (FtsQuery.BuildMatch(query) is null)
		{
			var listing = ListActive(ctx, typeFilter);
			var scoredListing = listing.Select((e, i) => (e, 1.0 / (i + 1), true)).ToList();
			return (scoredListing, new SearchRetrievers(true, false, false));
		}

		await EnsureLexicalBackfillAsync(ctx, projectKey, ct);

		Func<DataConnection> connect = () => _stores.NewConnection(projectKey, store);
		var indexes = new List<ISearchIndex>();
		if (lexical != false)
			indexes.Add(new SqliteFtsIndex(connect));
		if (semantic != false && _llm is not null)
			indexes.Add(new VectorSearchIndex(connect, new LlmClientEmbedder(_llm, projectKey), VectorDim));

		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(null), k, ct);

		// Resolve hits to entries (preserving fused order + score) and apply the MemoryType filter.
		// The fused hit's Retriever names the FIRST index that surfaced it; the lexical index is
		// always registered before the vector one (below), so "lexical" ⟺ the lexical leg confirmed
		// this hit and "semantic" ⟺ it was surfaced by the vector leg ALONE (unconfirmed noise a
		// caller may floor). No lexical leg (lexical:false) → nothing is confirmed.
		var ids = resp.Hits.Select(h => h.Id).ToList();
		var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
		var score = resp.Hits.ToDictionary(h => h.Id, h => h.Score);
		var confirmed = resp.Hits.ToDictionary(h => h.Id, h => h.Retriever == "lexical");
		var hits = ctx.Entries.Where(e => e.ActiveTo == null && ids.Contains(e.Key)).ToList()
			.Where(e => typeFilter == null || e.Type == typeFilter)
			.OrderBy(e => order[e.Key])
			.Select(e => (e, score[e.Key], confirmed[e.Key]))
			.ToList();

		return (hits, resp.Retrievers);
	}

	// READ snippet: bodyLen <= 0 -> the full body; otherwise the first N chars with "…"
	// appended when cut (mirrors ModuleMcp.SnippetBody — read returns content by default).
	static string SnippetBody(string body, int bodyLen) =>
		bodyLen <= 0 || body.Length <= bodyLen ? body : string.Concat(body.AsSpan(0, bodyLen), "…");

	public async Task<MemoryUpsertOutcome> UpsertAsync(string projectKey, string store, IReadOnlyList<MemoryEntryInput> upserts, IReadOnlyList<MemoryDelete> deletes, CancellationToken ct = default)
	{
		using var op = PetBoxActivitySources.Memory.StartActivity("memory.upsert");
		op?.SetTag("petbox.project", projectKey);
		op?.SetTag("petbox.store", store);
		op?.SetTag("petbox.upsert_count", upserts.Count);
		op?.SetTag("petbox.delete_count", deletes.Count);

		EnforceCanonBudget(store, upserts); // reject an oversized canon body before we vivify the store

		await _stores.EnsureAsync(projectKey, store, ct); // auto-vivify on first write
		var ctx = _stores.GetContext(projectKey, store);
		var desired = MergePatches(ctx, upserts);
		var dels = deletes.Select(d => (d.Key, d.Version)).ToArray();
		var fts = new SqliteFtsIndex(() => ctx); // writes ride the tx below; connect unused

		// Class-A lexical floor is updated INSIDE the entity transaction: the just-inserted
		// revisions are (re)indexed and soft-deleted keys dropped, all committing/rolling back
		// with the entity. Class-B vectors are NOT touched here — the worker materializes them.
		var r = await TemporalStore.UpsertAsync(ctx, desired, dels, 0,
			onWithinTx: async (tx, upserted, deletedKeys, c) =>
			{
				foreach (var e in upserted)
					await fts.IndexAsync(tx, MemorySearchDocs.ToDoc(e, projectKey), c);
				foreach (var key in deletedKeys)
					await fts.DeleteAsync(tx, projectKey, MemorySearchDocs.Type, key, c);
			},
			ct: ct);

		// Concurrency outcomes as COUNTS/kinds only (never values) — makes Stale/auto-resolve
		// measurable in telemetry (intake stale-baseline-blind-retry).
		if (r.Conflicts.Count > 0)
		{
			op?.SetTag("petbox.conflicts", r.Conflicts.Count);
			op?.SetTag("petbox.conflict_kinds", string.Join(",", r.Conflicts.Select(c => c.Kind.ToString()).Distinct()));
		}
		if (r.AutoResolved.Count > 0)
			op?.SetTag("petbox.auto_resolved", r.AutoResolved.Count);

		if (r.Applied)
			await _stores.TouchAsync(projectKey, store, ct);
		return new MemoryUpsertOutcome(ScopeEchoToCall(r, upserts, deletes));
	}

	// Canon write-gate (spec agent-wiring, memory-canon-storage): the canon store is a curated
	// index pulled into every agent session, so an entry body over the budget is REJECTED with an
	// educational message rather than bloating the pull. Only bodies actually supplied are measured
	// — an omitted body on a PATCH edit keeps the current (already-within-budget) value. Other
	// stores are never touched by this gate.
	static void EnforceCanonBudget(string store, IReadOnlyList<MemoryEntryInput> upserts)
	{
		if (!string.Equals(store, CanonStore, StringComparison.OrdinalIgnoreCase)) return;
		foreach (var u in upserts)
			if (u.Body is { Length: > CanonBodyBudget })
				throw new ArgumentException(
					$"canon entry '{u.Key}' body is {u.Body.Length} chars, over the {CanonBodyBudget}-char budget. " +
					"The memory canon must stay a COMPACT INDEX of pointers — short entries that link to detail " +
					"elsewhere (task boards, docs, other memory stores), not a second knowledge base. Trim it to a " +
					"lean index of references.");
	}

	// Scope a write echo to THIS call (spec sinceversion-contract — the write-ack carries no
	// delta and no cursor parameter): keep only entries whose key the call mentioned (upsert
	// keys + rename sources) and only the deletes it sent. Memory has no cascade effects, so
	// key scoping is complete. CurrentVersion (the store-wide cursor for DeltaAsync) is
	// untouched.
	static TemporalUpsertResult<MemoryEntry> ScopeEchoToCall(TemporalUpsertResult<MemoryEntry> r,
		IReadOnlyList<MemoryEntryInput> upserts, IReadOnlyList<MemoryDelete> deletes)
	{
		// A write that did NOT apply changed nothing — the echo must be empty so the ack reads
		// unambiguously as "not applied" (spec upsert-ack-echo-clean); the conflict (with its
		// baseline/active version) is the whole story, and a caller rebases via memory_delta.
		if (!r.Applied)
			return r with { Added = [], Updated = [], Removed = [] };

		var mentioned = upserts.Select(u => u.Key)
			.Concat(upserts.Where(u => u.PrevKey is not null).Select(u => u.PrevKey!))
			.ToHashSet(StringComparer.Ordinal);
		var deleted = deletes.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
		return r with
		{
			Added = r.Added.Where(e => mentioned.Contains(e.Key)).ToList(),
			Updated = r.Updated.Where(e => mentioned.Contains(e.Key)).ToList(),
			Removed = r.Removed.Where(k => deleted.Contains(k)).ToList(),
		};
	}

	public async Task<MemoryUpsertOutcome> DeltaAsync(string projectKey, string store, long sinceVersion, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var ctx = _stores.GetContext(projectKey, store);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<MemoryEntry>(), sinceVersion, ct: ct);
		return new MemoryUpsertOutcome(r);
	}

	public async Task<IReadOnlyList<MemoryEntry>> ListActiveEntriesAsync(string projectKey, string store, CancellationToken ct = default)
	{
		var ctx = _stores.GetContext(projectKey, store);
		return ctx.Entries.Where(e => e.ActiveTo == null).OrderBy(e => e.Key).ToList();
	}

	public async Task<IReadOnlyDictionary<string, MemoryUsageView>> GetUsageAsync(string projectKey, string store,
		IReadOnlyCollection<string>? keys = null, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var ctx = _stores.GetContext(projectKey, store);
		var q = ctx.Usage.AsQueryable();
		if (keys is not null) q = q.Where(u => keys.Contains(u.Key));
		return q.ToList().ToDictionary(
			u => u.Key,
			u => new MemoryUsageView(u.SurfacedCount, u.OpenedCount, u.LastHitAt, u.DeliberateCount),
			StringComparer.Ordinal);
	}

	public async Task<MemoryUsageAggregate> GetUsageAggregateAsync(string projectKey, string store,
		int deadTailLimit = 10, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var ctx = _stores.GetContext(projectKey, store);

		// Active entries are the denominator (usage rows can outlive a soft-deleted key, so we
		// join FROM the active set, not the counter table). Key + Created is all the aggregate needs.
		var entries = ctx.Entries.Where(e => e.ActiveTo == null).Select(e => new { e.Key, e.Created }).ToList();
		var usage = ctx.Usage.ToList().ToDictionary(u => u.Key, StringComparer.Ordinal);

		var surfacedCount = 0;
		var deliberateCount = 0;
		var openedCount = 0;
		var surfacedHits = new List<DateTime>();
		var dead = new List<(string Key, DateTime Created)>();
		foreach (var e in entries)
		{
			usage.TryGetValue(e.Key, out var u);
			if (u is { SurfacedCount: > 0 })
			{
				surfacedCount++;
				if (u.LastHitAt is { } hit) surfacedHits.Add(hit);
			}
			else
			{
				dead.Add((e.Key, e.Created)); // never surfaced — a dead-tail candidate
			}
			if (u is { DeliberateCount: > 0 }) deliberateCount++; // honest value cut
			if (u is { OpenedCount: > 0 }) openedCount++;
		}

		var total = entries.Count;
		var deadTail = new MemoryDeadTail(
			dead.Count,
			dead.OrderBy(d => d.Created).ThenBy(d => d.Key, StringComparer.Ordinal)
				.Take(Math.Max(0, deadTailLimit)).Select(d => d.Key).ToList());
		return new MemoryUsageAggregate(
			TotalEntries: total,
			SurfacedAtLeastOnce: surfacedCount,
			DeliberatelySurfacedAtLeastOnce: deliberateCount,
			OpenedAtLeastOnce: openedCount,
			SurfacedFraction: total == 0 ? 0 : (double)surfacedCount / total,
			OpenedFraction: total == 0 ? 0 : (double)openedCount / total,
			MedianLastHitAt: Median(surfacedHits),
			DeadTail: deadTail);
	}

	// Median of an unordered timestamp list: null when empty, the middle element for an odd
	// count, the tick-average of the two middles for an even one (the textbook median; a
	// midpoint timestamp still reads as a sensible "typical last hit").
	static DateTime? Median(List<DateTime> hits)
	{
		if (hits.Count == 0) return null;
		hits.Sort();
		var mid = hits.Count / 2;
		return hits.Count % 2 == 1
			? hits[mid]
			: new DateTime((hits[mid - 1].Ticks + hits[mid].Ticks) / 2);
	}

	// ---- helpers ----

	async Task EnsureStore(string projectKey, string store, CancellationToken ct)
	{
		if (!await _stores.ExistsAsync(projectKey, store, ct))
			throw new InvalidOperationException($"memory store '{store}' not found in project '{projectKey}'");
	}

	static List<MemoryEntry> ListActive(MemoryDb ctx, MemoryType? typeFilter)
	{
		var q = ctx.Entries.Where(e => e.ActiveTo == null);
		if (typeFilter is not null) q = q.Where(e => e.Type == typeFilter.Value);
		return q.OrderBy(e => e.Key).ToList();
	}

	// One-time lexical backfill: entries written before the search retrofit have no search_fts
	// rows. Cheap and idempotent — guarded by a count so it runs at most once per file, rebuilt
	// in a single transaction from the same projection the write seam uses.
	static async Task EnsureLexicalBackfillAsync(MemoryDb ctx, string scope, CancellationToken ct)
	{
		if (ctx.Execute<long>("SELECT count(*) FROM search_fts") > 0) return;
		var active = ctx.Entries.Where(e => e.ActiveTo == null).ToList();
		if (active.Count == 0) return;

		var fts = new SqliteFtsIndex(() => ctx);
		using var tx = await ctx.BeginTransactionAsync(ct);
		try
		{
			foreach (var e in active)
				await fts.IndexAsync(ctx, MemorySearchDocs.ToDoc(e, scope), ct);
			await tx.CommitAsync(ct);
		}
		catch
		{
			await tx.RollbackAsync(ct);
			throw;
		}
	}

	// PATCH semantics (spec explicit-write-semantics): an EDIT (version > 0) merges against the
	// active entry it targets — an omitted (null) field keeps its current value; an explicitly
	// empty one ("" — for tags a blank CSV) clears it. A NEW entry (version 0) maps null to empty,
	// as before. This kills the silent-wipe class of bugs: a tags-only edit no longer empties
	// description/body. The merge base is read at the author's baseline; if the entry moves
	// before the write lands, TemporalStore's CAS yields a Stale conflict, so a merge against a
	// stale base can never be committed.
	static MemoryEntry[] MergePatches(MemoryDb ctx, IReadOnlyList<MemoryEntryInput> upserts)
	{
		var editKeys = upserts.Where(u => u.Version != 0).Select(u => u.PrevKey ?? u.Key).Distinct().ToList();
		var active = editKeys.Count == 0
			? new Dictionary<string, MemoryEntry>(StringComparer.Ordinal)
			: ctx.Entries.Where(e => e.ActiveTo == null && editKeys.Contains(e.Key)).ToList()
				.ToDictionary(e => e.Key, StringComparer.Ordinal);
		return upserts
			.Select(u => ToEntry(u, u.Version != 0 && active.TryGetValue(u.PrevKey ?? u.Key, out var c) ? c : null))
			.ToArray();
	}

	static MemoryEntry ToEntry(MemoryEntryInput i, MemoryEntry? current) => new()
	{
		Key = string.IsNullOrWhiteSpace(i.Key) ? throw new ArgumentException("key is required") : i.Key,
		Version = i.Version,
		Type = !string.IsNullOrWhiteSpace(i.Type) ? ParseType(i.Type) : current?.Type ?? throw new ArgumentException("type is required for new entries"),
		Description = i.Description ?? current?.Description ?? string.Empty,
		Body = i.Body ?? current?.Body ?? string.Empty,
		// PATCH: null = keep the current set; a non-null list (incl. []) REPLACES it.
		Tags = i.Tags is null ? current?.Tags ?? string.Empty : NormalizeTags(i.Tags),
		Metadata = i.Metadata ?? current?.Metadata ?? string.Empty,
		PrevKey = i.PrevKey,
	};

	static MemoryEntryView View(MemoryEntry e) =>
		new(e.Key, e.Type.ToString(), e.Description, e.Body, SplitTags(e.Tags), e.Version, e.Metadata);

	static MemoryType ParseType(string s) =>
		Enum.TryParse<MemoryType>(s, ignoreCase: true, out var v)
			? v
			: throw new ArgumentException($"invalid type '{s}' (User|Feedback|Project|Reference)");

	// The surface speaks tag ARRAYS; storage stays a CSV column (queryable, FTS-fed as-is).
	// Normalised at this boundary on write: trim, lowercase, drop blanks, de-dup, join.
	static string NormalizeTags(IReadOnlyList<string> tags) =>
		string.Join(',', tags
			.Where(t => !string.IsNullOrWhiteSpace(t))
			.Select(t => t.Trim().ToLowerInvariant())
			.Distinct());

	// Storage CSV -> the surface array (empty column = empty array).
	static string[] SplitTags(string csv) =>
		string.IsNullOrWhiteSpace(csv)
			? []
			: csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
