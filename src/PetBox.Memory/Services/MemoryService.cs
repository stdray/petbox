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

	readonly IMemoryStore _stores;
	// Optional embedding capability (DI auto-fills when an LLM router is registered).
	// Null → semantic search disabled (lexical-only); never throws.
	readonly ILlmClient? _llm;

	public MemoryService(IMemoryStore stores, ILlmClient? llm = null)
	{
		_stores = stores;
		_llm = llm;
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
		return new MemorySearchResult(hits.Select(View).ToList(), retrievers);
	}

	// ---- unified read (list = search without a query; uniform-entity-verbs v2) ----

	// Stores skipped by the implicit "every store" sweep of the unified read: sensitive
	// operational stores that must never be auto-pulled into an agent's context (e.g. "ops"
	// has held secrets). An explicit Filter.Store still reaches them — only the implicit
	// sweep excludes.
	static readonly HashSet<string> SweepExcludedStores = new(StringComparer.OrdinalIgnoreCase) { "ops" };

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
			? (await _stores.ListAsync(projectKey, ct)).Select(s => s.Name).Where(n => !SweepExcludedStores.Contains(n)).ToList()
			: [f.Store!.Trim()];

		var selected = new List<(string Store, MemoryEntry Entry)>();
		SearchRetrievers? retrievers = null;
		foreach (var store in stores)
		{
			ct.ThrowIfCancellationRequested();
			if (!await _stores.ExistsAsync(projectKey, store, ct)) continue;
			if (query is null)
			{
				// LISTING: the active entries of the store (deterministic; ordered below).
				selected.AddRange(ListActive(_stores.GetContext(projectKey, store), typeFilter).Select(e => (store, e)));
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
				selected.AddRange(hits.Select(e => (store, e)));
			}
		}
		if (query is not null) retrievers ??= new SearchRetrievers(false, false, false);

		selected = SortSelected(selected, request.Sort, hasQuery: query is not null);
		if (request.Limit > 0 && selected.Count > request.Limit) selected = selected.Take(request.Limit).ToList();

		var hits2 = selected.Select(x => new MemoryEntryHit(x.Store,
			request.BodyLen > 0 ? View(x.Entry) with { Body = SnippetBody(x.Entry.Body, request.BodyLen) } : View(x.Entry))).ToList();
		return new MemoryEntrySearchResult(hits2, retrievers);
	}

	// Final ordering of the selected set. No sort: query mode keeps the per-store fused
	// order (stores in list order — the old recall semantics), a listing defaults to
	// Updated desc (the freshest fact first — keys are opaque generated ids, so key order
	// carries no meaning). An explicit created/updated sort reorders WITHIN the selected
	// set; Relevance = keep the fused order (guarded to query mode; `desc` is meaningless
	// there and ignored). Ties break on key then store for determinism.
	static List<(string Store, MemoryEntry Entry)> SortSelected(
		List<(string Store, MemoryEntry Entry)> hits, (MemorySortBy By, bool Desc)? sort, bool hasQuery)
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

	static List<(string Store, MemoryEntry Entry)> Ordered<TKey>(
		List<(string Store, MemoryEntry Entry)> hits, Func<(string Store, MemoryEntry Entry), TKey> key, bool desc) =>
		(desc ? hits.OrderByDescending(key) : hits.OrderBy(key))
			.ThenBy(x => x.Entry.Key, StringComparer.Ordinal)
			.ThenBy(x => x.Store, StringComparer.Ordinal)
			.ToList();

	// Hybrid selection over ONE store's open entries (lexical FTS5 ⊕ semantic vectors,
	// RRF-fused), entities returned in fused order with retriever provenance. `k` bounds
	// the candidate pool. The type filter applies post-resolution (Type is constant in
	// the index, as before).
	async Task<(List<MemoryEntry> Hits, SearchRetrievers Retrievers)> SearchStoreAsync(
		string projectKey, string store, string query, MemoryType? typeFilter, int k,
		bool? lexical, bool? semantic, CancellationToken ct)
	{
		var ctx = _stores.GetContext(projectKey, store);

		// No searchable tokens (empty/punctuation query): degrade to a type-filtered listing —
		// a filter-only query still returns a sensible set rather than nothing (preserved from
		// the pre-contract behaviour; FtsQuery returns no match for such queries).
		if (FtsQuery.BuildMatch(query) is null)
			return (ListActive(ctx, typeFilter), new SearchRetrievers(true, false, false));

		await EnsureLexicalBackfillAsync(ctx, projectKey, ct);

		Func<DataConnection> connect = () => _stores.NewConnection(projectKey, store);
		var indexes = new List<ISearchIndex>();
		if (lexical != false)
			indexes.Add(new SqliteFtsIndex(connect));
		if (semantic != false && _llm is not null)
			indexes.Add(new VectorSearchIndex(connect, new LlmClientEmbedder(_llm, projectKey), VectorDim));

		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(null), k, ct);

		// Resolve hits to entries (preserving fused order) and apply the MemoryType filter here.
		var ids = resp.Hits.Select(h => h.Id).ToList();
		var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
		var hits = ctx.Entries.Where(e => e.ActiveTo == null && ids.Contains(e.Key)).ToList()
			.Where(e => typeFilter == null || e.Type == typeFilter)
			.OrderBy(e => order[e.Key])
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

		if (r.Applied)
			await _stores.TouchAsync(projectKey, store, ct);
		return new MemoryUpsertOutcome(ScopeEchoToCall(r, upserts, deletes));
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
			u => new MemoryUsageView(u.SurfacedCount, u.OpenedCount, u.LastHitAt),
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
		Type = ParseType(i.Type),
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
