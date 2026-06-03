using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.DataProvider.SQLite;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
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

	public MemoryService(IMemoryStore stores) => _stores = stores;

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

	public async Task<IReadOnlyList<MemoryEntryView>> SearchAsync(string projectKey, string store, string query, string? type, CancellationToken ct = default)
	{
		await EnsureStore(projectKey, store, ct);
		var ctx = _stores.GetContext(projectKey, store);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);

		var match = BuildMatch(query);
		if (match is null)
		{
			// No searchable tokens — degrade to a type-filtered listing.
			var allQ = ctx.Entries.Where(e => e.ActiveTo == null);
			if (typeFilter is not null) allQ = allQ.Where(e => e.Type == typeFilter.Value);
			return allQ.OrderBy(e => e.Key).ToList().Select(View).ToList();
		}

		// FTS5 MATCH + rank ordering via linq2db's SQLite extensions.
		var ranked = ctx.MemoryFts
			.Where(f => Sql.Ext.SQLite().Match(f, match))
			.OrderBy(f => Sql.Ext.SQLite().Rank(f))
			.Select(f => f.Key)
			.ToList();
		if (ranked.Count == 0)
			return [];

		var order = ranked.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
		return ctx.Entries.Where(e => e.ActiveTo == null && ranked.Contains(e.Key)).ToList()
			.Where(e => typeFilter == null || e.Type == typeFilter)
			.OrderBy(e => order[e.Key])
			.Select(View)
			.ToList();
	}

	public async Task<MemoryUpsertOutcome> UpsertAsync(string projectKey, string store, IReadOnlyList<MemoryEntryInput> upserts, IReadOnlyList<MemoryDelete> deletes, long sinceVersion = 0, CancellationToken ct = default)
	{
		await _stores.EnsureAsync(projectKey, store, ct); // auto-vivify on first write
		var desired = upserts.Select(ToEntry).ToArray();
		var dels = deletes.Select(d => (d.Key, d.Version)).ToArray();
		var ctx = _stores.GetContext(projectKey, store);
		var r = await TemporalStore.UpsertAsync(ctx, desired, dels, sinceVersion, ct: ct);
		if (r.Applied) { RebuildFts(ctx); await _stores.TouchAsync(projectKey, store, ct); }
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

	// Lenient FTS5 MATCH expression: alnum tokens, prefix-matched (tok*) and ANDed.
	// Null when there's nothing to match (caller degrades to a plain listing).
	static string? BuildMatch(string? query)
	{
		if (string.IsNullOrWhiteSpace(query)) return null;
		var tokens = NonAlnum().Matches(query.ToLowerInvariant()).Select(m => m.Value + "*");
		var joined = string.Join(' ', tokens);
		return joined.Length == 0 ? null : joined;
	}

	[GeneratedRegex("[a-z0-9]+")]
	private static partial Regex NonAlnum();
}
