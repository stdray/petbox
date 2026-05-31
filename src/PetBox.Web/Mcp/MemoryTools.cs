using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.DataProvider.SQLite;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Memory.Data;

namespace PetBox.Web.Mcp;

// MCP surface for the Memory module: named store lifecycle + temporal entry
// content. v1 is project-scoped. Scopes: memory:read / memory:write. Feature: Memory.
[McpServerToolType]
public static class MemoryTools
{
	[McpServerTool(Name = "memory.store_create", Title = "Create a memory store")]
	[Description("Create a named memory store in a project. Requires memory:write.")]
	public static async Task<object> StoreCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, string? description = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var meta = await stores.CreateAsync(projectKey, store, description, ct);
		return new { meta.ProjectKey, meta.Name, meta.Description, meta.CreatedAt };
	}

	[McpServerTool(Name = "memory.store_list", Title = "List memory stores", ReadOnly = true)]
	[Description("List memory stores in a project. Requires memory:read.")]
	public static async Task<object> StoreListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var list = await stores.ListAsync(projectKey, ct);
		return new { stores = list.Select(s => new { s.Name, s.Description, s.CreatedAt }).ToList() };
	}

	[McpServerTool(Name = "memory.store_delete", Title = "Delete a memory store", Destructive = true)]
	[Description("Delete a memory store and its entries. Requires memory:write.")]
	public static async Task<object> StoreDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		return new { deleted = await stores.DeleteAsync(projectKey, store, ct) };
	}

	[McpServerTool(Name = "memory.list", Title = "List memory entries", ReadOnly = true)]
	[Description("List active entries of a memory store, ordered by key. Optional `type` filter (User|Feedback|Project|Reference). Requires memory:read.")]
	public static async Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, string? type = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		await EnsureStore(stores, projectKey, store, ct);
		var ctx = stores.GetContext(projectKey, store);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);
		var q = ctx.Entries.Where(e => e.ActiveTo == null);
		if (typeFilter is not null) q = q.Where(e => e.Type == typeFilter.Value);
		var active = q.OrderBy(e => e.Key).ToList();
		return new { entries = active.Select(EntryDto).ToList() };
	}

	[McpServerTool(Name = "memory.get", Title = "Get a memory entry", ReadOnly = true)]
	[Description("Get the active entry by key, or null. Requires memory:read.")]
	public static async Task<object?> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, string key, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		await EnsureStore(stores, projectKey, store, ct);
		var ctx = stores.GetContext(projectKey, store);
		var e = ctx.Entries.Where(x => x.Key == key && x.ActiveTo == null).ToList().FirstOrDefault();
		return e is null ? null : EntryDto(e);
	}

	[McpServerTool(Name = "memory.search", Title = "Search memory entries", ReadOnly = true)]
	[Description("FTS5 full-text search over active entries' description/body/tags, ranked by relevance. Matches by token (prefix), so paraphrases hit. Optional `type` filter (User|Feedback|Project|Reference). Requires memory:read.")]
	public static async Task<object> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, string query, string? type = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		await EnsureStore(stores, projectKey, store, ct);
		var ctx = stores.GetContext(projectKey, store);
		var typeFilter = type is null ? (MemoryType?)null : ParseType(type);

		var match = BuildMatch(query);
		if (match is null)
		{
			// No searchable tokens — degrade to a type-filtered listing.
			var allQ = ctx.Entries.Where(e => e.ActiveTo == null);
			if (typeFilter is not null) allQ = allQ.Where(e => e.Type == typeFilter.Value);
			return new { entries = allQ.OrderBy(e => e.Key).ToList().Select(EntryDto).ToList() };
		}

		// FTS5 MATCH + rank ordering via linq2db's SQLite extensions.
		var ranked = ctx.MemoryFts
			.Where(f => Sql.Ext.SQLite().Match(f, match))
			.OrderBy(f => Sql.Ext.SQLite().Rank(f))
			.Select(f => f.Key)
			.ToList();
		if (ranked.Count == 0)
			return new { entries = Array.Empty<object>() };

		var order = ranked.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
		var hits = ctx.Entries.Where(e => e.ActiveTo == null && ranked.Contains(e.Key)).ToList()
			.Where(e => typeFilter == null || e.Type == typeFilter)
			.OrderBy(e => order[e.Key])
			.ToList();
		return new { entries = hits.Select(EntryDto).ToList() };
	}

	[McpServerTool(Name = "memory.upsert", Title = "Upsert memory entries")]
	[Description("""
		Declarative temporal upsert of entries into a store. Requires memory:write.
		`entries` is a JSON array of { key, type, description, body, tags?, version?, prevKey? }.
		`type` (required) is the taxonomy: User (about the user) | Feedback (a correction/
		preference on how to work) | Project (durable project fact/constraint) | Reference
		(pointer to an external resource). Pick one. `tags` is free CSV, normalised on write.
		`version` is the baseline you last saw (0 = new). Set `prevKey` to rename.
		Store durable facts not derivable from code/git/config; actionable work goes to a
		task board, not here.
		Result: { applied, currentVersion, inserted, closed, conflicts[], added[], updated[], removed[] }.
		""")]
	public static async Task<object> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store,
		[Description("JSON array of entry objects")] JsonElement entries,
		long sinceVersion = 0, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		await stores.EnsureAsync(projectKey, store, ct); // auto-vivify on first write
		var desired = ParseEntries(entries);
		var ctx = stores.GetContext(projectKey, store);
		var r = await TemporalStore.UpsertAsync(ctx, desired, sinceVersion, ct: ct);
		if (r.Applied) RebuildFts(ctx);
		return Serialize(r);
	}

	[McpServerTool(Name = "memory.delta", Title = "Memory delta since cursor", ReadOnly = true)]
	[Description("Return entries added/updated/removed since `sinceVersion` (no writes). Requires memory:read.")]
	public static async Task<object> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, long sinceVersion, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		await EnsureStore(stores, projectKey, store, ct);
		var ctx = stores.GetContext(projectKey, store);
		var r = await TemporalStore.UpsertAsync(ctx, Array.Empty<MemoryEntry>(), sinceVersion, ct: ct);
		return Serialize(r);
	}

	static async Task EnsureStore(IMemoryStore stores, string projectKey, string store, CancellationToken ct)
	{
		if (!await stores.ExistsAsync(projectKey, store, ct))
			throw new InvalidOperationException($"memory store '{store}' not found in project '{projectKey}'");
	}

	static object Serialize(TemporalUpsertResult<MemoryEntry> r) => new
	{
		applied = r.Applied,
		currentVersion = r.CurrentVersion,
		inserted = r.Inserted,
		closed = r.Closed,
		conflicts = r.Conflicts.Select(c => new
		{
			key = c.Key,
			kind = c.Kind.ToString(),
			baselineVersion = c.BaselineVersion,
			activeVersion = c.ActiveVersion,
		}).ToList(),
		added = r.Added.Select(EntryDto).ToList(),
		updated = r.Updated.Select(EntryDto).ToList(),
		removed = r.Removed.ToList(),
	};

	static object EntryDto(MemoryEntry e) => new
	{
		key = e.Key,
		type = e.Type.ToString(),
		description = e.Description,
		body = e.Body,
		tags = e.Tags,
		version = e.Version,
	};

	static MemoryEntry[] ParseEntries(JsonElement entries)
	{
		if (entries.ValueKind != JsonValueKind.Array)
			throw new ArgumentException("entries must be a JSON array");
		var list = new List<MemoryEntry>();
		foreach (var e in entries.EnumerateArray())
		{
			list.Add(new MemoryEntry
			{
				Key = ModuleMcp.ReqStr(e, "key"),
				Version = ModuleMcp.OptLong(e, "version", 0),
				Type = ParseType(ModuleMcp.ReqStr(e, "type")),
				Description = ModuleMcp.OptStr(e, "description") ?? string.Empty,
				Body = ModuleMcp.OptStr(e, "body") ?? string.Empty,
				Tags = NormalizeTags(ModuleMcp.OptStr(e, "tags")),
				PrevKey = ModuleMcp.OptStr(e, "prevKey"),
			});
		}
		return list.ToArray();
	}

	static MemoryType ParseType(string s) =>
		Enum.TryParse<MemoryType>(s, ignoreCase: true, out var v)
			? v
			: throw new ArgumentException($"invalid type '{s}' (User|Feedback|Project|Reference)");

	// Free CSV tags, normalised on write: split on comma, trim, lowercase, drop
	// blanks, de-dup, re-join. Keeps the column queryable and stops accidental
	// case/whitespace duplicates ("Go" vs "go ").
	static string NormalizeTags(string? raw) =>
		string.IsNullOrWhiteSpace(raw)
			? string.Empty
			: string.Join(',', raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(t => t.ToLowerInvariant())
				.Distinct());

	// The FTS5 mirror only holds the current active set; rebuild it wholesale after
	// a write (stores are small — avoids temporal-aware trigger plumbing).
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
		var tokens = Regex.Matches(query.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value + "*");
		var joined = string.Join(' ', tokens);
		return joined.Length == 0 ? null : joined;
	}
}
