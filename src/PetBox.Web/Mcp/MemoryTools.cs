using System.ComponentModel;
using System.Text.Json;
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
	[Description("List active entries of a memory store, ordered by key. Requires memory:read.")]
	public static async Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		await EnsureStore(stores, projectKey, store, ct);
		var ctx = stores.GetContext(projectKey, store);
		var active = ctx.Entries.Where(e => e.ActiveTo == null).OrderBy(e => e.Key).ToList();
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
	[Description("Substring search over active entries' description/body/tags. Requires memory:read.")]
	public static async Task<object> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryStore stores,
		string projectKey, string store, string query, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		await EnsureStore(stores, projectKey, store, ct);
		var ctx = stores.GetContext(projectKey, store);
		var q = query ?? string.Empty;
		var hits = ctx.Entries.Where(e => e.ActiveTo == null).ToList()
			.Where(e => e.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
				|| e.Body.Contains(q, StringComparison.OrdinalIgnoreCase)
				|| e.Tags.Contains(q, StringComparison.OrdinalIgnoreCase))
			.OrderBy(e => e.Key)
			.ToList();
		return new { entries = hits.Select(EntryDto).ToList() };
	}

	[McpServerTool(Name = "memory.upsert", Title = "Upsert memory entries")]
	[Description("""
		Declarative temporal upsert of entries into a store. Requires memory:write.
		`entries` is a JSON array of { key, description, body, tags?, version?, prevKey? }.
		`version` is the baseline you last saw (0 = new). Set `prevKey` to rename.
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
		await EnsureStore(stores, projectKey, store, ct);
		var desired = ParseEntries(entries);
		var ctx = stores.GetContext(projectKey, store);
		var r = await TemporalStore.UpsertAsync(ctx, desired, sinceVersion, ct: ct);
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
				Description = ModuleMcp.OptStr(e, "description") ?? string.Empty,
				Body = ModuleMcp.OptStr(e, "body") ?? string.Empty,
				Tags = ModuleMcp.OptStr(e, "tags") ?? string.Empty,
				PrevKey = ModuleMcp.OptStr(e, "prevKey"),
			});
		}
		return list.ToArray();
	}
}
