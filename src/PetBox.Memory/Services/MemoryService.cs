using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.DataProvider.SQLite;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;

namespace PetBox.Memory.Services;

// The one implementation of IMemoryService: the single door to the memory store.
// All the entry logic that used to live in the MCP tool layer (taxonomy parsing,
// tag normalization, FTS5 search + rebuild, temporal upsert) lives here, so the MCP
// tools and the Razor store page share exactly one code path into the data.
public sealed partial class MemoryService : IMemoryService
{
	readonly IMemoryStore _stores;
	// Optional embedding capability (DI auto-fills when an LLM router is registered).
	// Null → semantic search disabled and embed-on-write skipped (lexical-only); never throws.
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
		var q = ctx.Entries.Where(e => e.ActiveTo == null);
		if (typeFilter is not null) q = q.Where(e => e.Type == typeFilter.Value);
		return q.OrderBy(e => e.Key).ToList().Select(View).ToList();
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

		// null = enabled; semantic also requires an embedding capability to be wired.
		var lexicalEnabled = lexical != false;
		var semanticEnabled = semantic != false && _llm is not null;

		IReadOnlyList<string>? lexicalKeys = null;
		var lexicalRan = false;
		if (lexicalEnabled)
		{
			lexicalKeys = LexicalKeys(ctx, query, typeFilter);
			lexicalRan = true;
		}

		IReadOnlyList<string>? semanticKeys = null;
		var semanticRan = false;
		var degraded = false;
		if (semanticEnabled)
		{
			try
			{
				var qr = await _llm!.EmbedAsync(projectKey, new EmbedRequest(new[] { query }), ct);
				var q = qr.Vectors[0];
				var qmodel = qr.Model.Model;
				var qdim = q.Length; // hoist: array-length isn't SQL-translatable inside Where
				// Model/dim guard: only fuse candidates embedded by the same model at the same
				// dim as the query, so we never cosine-compare incomparable vectors.
				var rows = ctx.MemoryVec.Where(v => v.Model == qmodel && v.Dim == qdim).ToList();
				var top = VectorMath.TopK(q, rows.Select(r => (r.Key, VectorCodec.Decode(r.Vec))), 50);
				semanticKeys = top.Select(t => t.Key).ToList();
				semanticRan = true;
			}
			catch
			{
				// Embedding unavailable at query time → degrade to whatever else ran.
				semanticRan = false;
				degraded = true;
			}
		}

		// RRF-fuse the retrievers that ran; one retriever passes through unchanged.
		IReadOnlyList<string> fused =
			lexicalRan && semanticRan ? HybridMerge.Rrf(lexicalKeys, semanticKeys)
			: lexicalRan ? lexicalKeys ?? []
			: semanticRan ? semanticKeys ?? []
			: [];

		var order = fused.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
		var hits = ctx.Entries.Where(e => e.ActiveTo == null && fused.Contains(e.Key)).ToList()
			.Where(e => typeFilter == null || e.Type == typeFilter)
			.OrderBy(e => order[e.Key])
			.Select(View)
			.ToList();

		return new MemorySearchResult(hits, new SearchRetrievers(lexicalRan, semanticRan, degraded));
	}

	// Lexical retriever as ordered keys (uniform shape for fusion). Preserves the
	// "no searchable tokens → type-filtered listing" degrade so an empty/symbol query
	// still returns a sensible set rather than nothing.
	static List<string> LexicalKeys(MemoryDb ctx, string query, MemoryType? typeFilter)
	{
		var match = BuildMatch(query);
		if (match is null)
		{
			var allQ = ctx.Entries.Where(e => e.ActiveTo == null);
			if (typeFilter is not null) allQ = allQ.Where(e => e.Type == typeFilter.Value);
			return allQ.OrderBy(e => e.Key).Select(e => e.Key).ToList();
		}

		// FTS5 MATCH + rank ordering via linq2db's SQLite extensions.
		return ctx.MemoryFts
			.Where(f => Sql.Ext.SQLite().Match(f, match))
			.OrderBy(f => Sql.Ext.SQLite().Rank(f))
			.Select(f => f.Key)
			.ToList();
	}

	public async Task<MemoryUpsertOutcome> UpsertAsync(string projectKey, string store, IReadOnlyList<MemoryEntryInput> upserts, IReadOnlyList<MemoryDelete> deletes, long sinceVersion = 0, CancellationToken ct = default)
	{
		await _stores.EnsureAsync(projectKey, store, ct); // auto-vivify on first write
		var desired = upserts.Select(ToEntry).ToArray();
		var dels = deletes.Select(d => (d.Key, d.Version)).ToArray();
		var ctx = _stores.GetContext(projectKey, store);
		var r = await TemporalStore.UpsertAsync(ctx, desired, dels, sinceVersion, ct: ct);
		if (r.Applied)
		{
			RebuildFts(ctx);
			await EmbedOnWriteAsync(ctx, projectKey, desired, deletes, ct);
			await _stores.TouchAsync(projectKey, store, ct);
		}
		return new MemoryUpsertOutcome(r);
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

	// ---- helpers ----

	async Task EnsureStore(string projectKey, string store, CancellationToken ct)
	{
		if (!await _stores.ExistsAsync(projectKey, store, ct))
			throw new InvalidOperationException($"memory store '{store}' not found in project '{projectKey}'");
	}

	MemoryEntry ToEntry(MemoryEntryInput i) => new()
	{
		Key = string.IsNullOrWhiteSpace(i.Key) ? throw new ArgumentException("key is required") : i.Key,
		Version = i.Version,
		Type = ParseType(i.Type),
		Description = i.Description ?? string.Empty,
		Body = i.Body ?? string.Empty,
		Tags = NormalizeTags(i.Tags),
		Metadata = i.Metadata ?? string.Empty,
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

	// The FTS5 mirror only holds the current active set; rebuild it wholesale after a
	// write (stores are small — avoids temporal-aware trigger plumbing).
	static void RebuildFts(MemoryDb ctx)
	{
		ctx.MemoryFts.Delete();
		ctx.Entries.Where(e => e.ActiveTo == null)
			.Insert(ctx.MemoryFts, e => new MemoryFts
			{
				Key = e.Key,
				Description = e.Description,
				Body = e.Body,
				Tags = e.Tags,
			});
	}

	// Embed-on-write: refresh the vector mirror for the just-applied set. Each upserted
	// entry is (re)embedded from description+body; each deleted key's vector is dropped.
	// Best-effort: when no LLM capability is wired this is skipped entirely, and an embed
	// failure for one entry degrades that entry to lexical-only — it MUST NOT throw out of
	// the write path (a memory upsert must never fail because embedding was unavailable).
	async Task EmbedOnWriteAsync(MemoryDb ctx, string projectKey, IReadOnlyList<MemoryEntry> desired, IReadOnlyList<MemoryDelete> deletes, CancellationToken ct)
	{
		if (_llm is null) return;

		foreach (var d in deletes)
			ctx.MemoryVec.Delete(x => x.Key == d.Key);

		foreach (var e in desired)
		{
			try
			{
				var text = e.Description + "\n" + e.Body;
				var res = await _llm.EmbedAsync(projectKey, new EmbedRequest(new[] { text }), ct);
				var vec = res.Vectors[0];
				var row = new MemoryVec
				{
					Key = e.Key,
					Model = res.Model.Model,
					Dim = vec.Length,
					Vec = VectorCodec.Encode(vec),
				};
				ctx.MemoryVec.Delete(x => x.Key == e.Key);
				ctx.Insert(row);
			}
			catch
			{
				// Degrade: this entry stays lexical-only until a later upsert succeeds.
			}
		}
	}

	// Lenient FTS5 MATCH expression: Unicode word tokens (letters/digits — Latin,
	// Cyrillic, …), prefix-matched (tok*) and ANDed. The store's FTS5 table is unicode61
	// (case-folds + strips diacritics), so the query tokenizer must NOT drop non-ASCII:
	// a `[a-z0-9]` class would silently discard a Russian query and return nothing.
	// Prefix-* also softens the lack of stemming for ru/en. Null when nothing to match.
	static string? BuildMatch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query)) return null;
		var tokens = WordToken().Matches(query.ToLowerInvariant()).Select(m => m.Value + "*");
		var joined = string.Join(' ', tokens);
		return joined.Length == 0 ? null : joined;
	}

	[GeneratedRegex(@"[\p{L}\p{Nd}]+")]
	private static partial Regex WordToken();
}
