using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;

namespace PetBox.Web.Mcp;

// MCP surface for the Memory module: named store lifecycle + temporal entry content.
// A THIN adapter — it asserts the scope/feature/project guards, parses the JSON entry
// payload, and delegates every domain decision (taxonomy, tags, FTS, temporal write)
// to IMemoryService. It must not touch the store or DB context directly (a NetArchTest
// enforces this). v1 is project-scoped. Scopes: memory:read / memory:write.
[McpServerToolType]
public static class MemoryTools
{
	[McpServerTool(Name = "memory.store_create", Title = "Create a memory store")]
	[Description("Create a named memory store in a project. Requires memory:write.")]
	public static async Task<object> StoreCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, string? description = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var meta = await memory.CreateStoreAsync(projectKey, store, description, ct);
		return new { meta.ProjectKey, meta.Name, meta.Description, meta.CreatedAt };
	}

	[McpServerTool(Name = "memory.store_list", Title = "List memory stores", ReadOnly = true)]
	[Description("List memory stores in a project. Requires memory:read.")]
	public static async Task<object> StoreListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var list = await memory.ListStoresAsync(projectKey, ct);
		return new { stores = list.Select(s => new { s.Name, s.Description, s.CreatedAt }).ToList() };
	}

	[McpServerTool(Name = "memory.store_delete", Title = "Delete a memory store", Destructive = true)]
	[Description("Delete a memory store and its entries. Requires memory:write.")]
	public static async Task<object> StoreDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		return new { deleted = await memory.DeleteStoreAsync(projectKey, store, ct) };
	}

	[McpServerTool(Name = "memory.list", Title = "List memory entries", ReadOnly = true)]
	[Description("List active entries of a memory store, ordered by key. Optional `type` filter (User|Feedback|Project|Reference). Requires memory:read.")]
	public static async Task<object> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, string? type = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		return new { entries = await memory.ListAsync(projectKey, store, type, ct) };
	}

	[McpServerTool(Name = "memory.get", Title = "Get a memory entry", ReadOnly = true)]
	[Description("Get the active entry by key, or null. Requires memory:read.")]
	public static async Task<object?> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, string key, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		return await memory.GetAsync(projectKey, store, key, ct);
	}

	[McpServerTool(Name = "memory.search", Title = "Search memory entries", ReadOnly = true)]
	[Description("FTS5 full-text search over active entries' description/body/tags, ranked by relevance. Matches by token (prefix), so paraphrases hit. Optional `type` filter (User|Feedback|Project|Reference). Requires memory:read.")]
	public static async Task<object> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, string query, string? type = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		return new { entries = await memory.SearchAsync(projectKey, store, query, type, ct) };
	}

	[McpServerTool(Name = "memory.upsert", Title = "Upsert memory entries")]
	[Description("""
		Declarative temporal upsert of entries into a store. Requires memory:write.
		`entries` is a JSON array of { key, type, description, body, tags?, version?, prevKey? }.
		`type` (required) is the taxonomy: User (about the user) | Feedback (a correction/
		preference on how to work) | Project (durable project fact/constraint) | Reference
		(pointer to an external resource). Pick one. `tags` is free CSV, normalised on write.
		`version` is the baseline you last saw (0 = new). Set `prevKey` to rename.
		To delete an entry, pass { key, deleted:true } (optional version baseline) — it is
		soft-closed (history kept) and appears in the result's `removed`.
		Store durable facts not derivable from code/git/config; actionable work goes to a
		task board, not here.
		Result: { applied, currentVersion, inserted, closed, conflicts[], added[], updated[], removed[] }.
		""")]
	public static async Task<object> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store,
		[Description("JSON array of entry objects")] JsonElement entries,
		long sinceVersion = 0, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var (upserts, deletes) = ParseEntries(entries);
		return Serialize(await memory.UpsertAsync(projectKey, store, upserts, deletes, sinceVersion, ct));
	});

	[McpServerTool(Name = "memory.delta", Title = "Memory delta since cursor", ReadOnly = true)]
	[Description("Return entries added/updated/removed since `sinceVersion` (no writes). Requires memory:read.")]
	public static async Task<object> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, long sinceVersion, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		return Serialize(await memory.DeltaAsync(projectKey, store, sinceVersion, ct));
	}

	// ---- adapter plumbing: JSON parsing + wire shaping (no domain logic) ----

	static object Serialize(MemoryUpsertOutcome o)
	{
		var r = o.Result;
		return new
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
	}

	static object EntryDto(MemoryEntry e) => new
	{
		key = e.Key,
		type = e.Type.ToString(),
		description = e.Description,
		body = e.Body,
		tags = e.Tags,
		version = e.Version,
	};

	// Parse the entry array into typed inputs. Taxonomy/tag normalization happens in the
	// service. MCP clients sometimes pass the array as a JSON *string*, so accept both.
	static (List<MemoryEntryInput> Upserts, List<MemoryDelete> Deletes) ParseEntries(JsonElement entries)
	{
		using var doc = entries.ValueKind == JsonValueKind.String
			? JsonDocument.Parse(entries.GetString() ?? "")
			: (JsonDocument?)null;
		var arr = doc?.RootElement ?? entries;
		if (arr.ValueKind != JsonValueKind.Array)
			throw new ArgumentException($"entries must be a JSON array (got {arr.ValueKind})");
		var upserts = new List<MemoryEntryInput>();
		var deletes = new List<MemoryDelete>();
		foreach (var e in arr.EnumerateArray())
		{
			// `deleted:true` soft-deletes the entry (only key + optional version needed).
			if (e.ValueKind == JsonValueKind.Object
				&& e.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True)
			{
				deletes.Add(new MemoryDelete(ModuleMcp.ReqStr(e, "key"), ModuleMcp.OptLong(e, "version", 0)));
				continue;
			}
			upserts.Add(new MemoryEntryInput
			{
				Key = ModuleMcp.ReqStr(e, "key"),
				Version = ModuleMcp.OptLong(e, "version", 0),
				Type = ModuleMcp.ReqStr(e, "type"),
				Description = ModuleMcp.OptStr(e, "description"),
				Body = ModuleMcp.OptStr(e, "body"),
				Tags = ModuleMcp.OptStr(e, "tags"),
				PrevKey = ModuleMcp.OptStr(e, "prevKey"),
			});
		}
		return (upserts, deletes);
	}
}
