using LinqToDB;
using LinqToDB.Data;
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
		var ctx = _stores.GetContext(projectKey, store);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);

		// No searchable tokens (empty/punctuation query): degrade to a type-filtered listing —
		// a filter-only query still returns a sensible set rather than nothing (preserved from
		// the pre-contract behaviour; FtsQuery returns no match for such queries).
		if (FtsQuery.BuildMatch(query) is null)
			return new MemorySearchResult(
				ListActive(ctx, typeFilter).Select(View).ToList(),
				new SearchRetrievers(true, false, false));

		await EnsureLexicalBackfillAsync(ctx, projectKey, ct);

		Func<DataConnection> connect = () => _stores.NewConnection(projectKey, store);
		var indexes = new List<ISearchIndex>();
		if (lexical != false)
			indexes.Add(new SqliteFtsIndex(connect));
		if (semantic != false && _llm is not null)
			indexes.Add(new VectorSearchIndex(connect, new LlmClientEmbedder(_llm, projectKey), VectorDim));

		var resp = await new SearchService(indexes).SearchAsync(projectKey, query, new SearchFilter(null), SearchK, ct);

		// Resolve hits to entries (preserving fused order) and apply the MemoryType filter here —
		// Type is constant in the index, so filtering is post-resolution, as before.
		var ids = resp.Hits.Select(h => h.Id).ToList();
		var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
		var hits = ctx.Entries.Where(e => e.ActiveTo == null && ids.Contains(e.Key)).ToList()
			.Where(e => typeFilter == null || e.Type == typeFilter)
			.OrderBy(e => order[e.Key])
			.Select(View)
			.ToList();

		return new MemorySearchResult(hits, resp.Retrievers);
	}

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
		Tags = i.Tags is null ? current?.Tags ?? string.Empty : NormalizeTags(i.Tags),
		Metadata = i.Metadata ?? current?.Metadata ?? string.Empty,
		PrevKey = i.PrevKey,
	};

	static MemoryEntryView View(MemoryEntry e) =>
		new(e.Key, e.Type.ToString(), e.Description, e.Body, e.Tags, e.Version, e.Metadata);

	static MemoryType ParseType(string s) =>
		Enum.TryParse<MemoryType>(s, ignoreCase: true, out var v)
			? v
			: throw new ArgumentException($"invalid type '{s}' (User|Feedback|Project|Reference)");

	// Free CSV tags, normalised on write: split on comma, trim, lowercase, drop blanks,
	// de-dup, re-join. Keeps the column queryable and stops case/whitespace duplicates.
	static string NormalizeTags(string? raw) =>
		string.IsNullOrWhiteSpace(raw)
			? string.Empty
			: string.Join(',', raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(t => t.ToLowerInvariant())
				.Distinct());
}
