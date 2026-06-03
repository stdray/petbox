using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Memory.Contract;

namespace PetBox.Web.Mcp;

// mem0 wire parameter names are snake_case (user_id/agent_id/run_id) — part of the
// external mem0-compatible API contract, so CA1707 (no underscores) is intentional.
#pragma warning disable CA1707

// mem0/OpenMemory-compatible MCP surface over the existing memory store. A THIN
// adapter: same guards as MemoryTools (feature/scope), delegates all domain logic to
// IMemoryService, and shapes results into mem0's { results: [...] } form. Scope mapping
// + id codec live in Mem0Map. `projectKey` is OPTIONAL: omitted, it defaults to the
// API key's single-project claim (so off-the-shelf mem0 clients that only send user_id
// work; a cross-project "*" key must pass it explicitly). MVP: infer=false honored;
// infer=true accepted but distillation deferred (idea memory/distillation, blocked by
// llm-router) so it stores verbatim — the infer flag is the seam.
// Scopes: memory:read / memory:write.
[McpServerToolType]
public static class Mem0Tools
{
	[McpServerTool(Name = "add_memory", Title = "Add a memory (mem0-compatible)")]
	[Description("mem0-compatible. Store a memory: `messages` (string or array of {role,content}) or `text` is stored verbatim under the `user_id` scope (`agent_id`/`run_id` become tags). `projectKey` is optional (defaults to the key's project). `infer` is accepted but distillation is deferred — verbatim for now. Requires memory:write.")]
	public static async Task<object> AddMemoryAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null,
		JsonElement? messages = null, string? text = null,
		string? user_id = null, string? agent_id = null, string? run_id = null,
		JsonElement? metadata = null, bool infer = false,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);

		var body = Mem0Map.UnwrapJson(messages) is { } m && m.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
			? Mem0Map.MessagesToBody(m)
			: text ?? string.Empty;
		if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("messages or text is required");

		var store = Mem0Map.StoreFromUserId(user_id);
		var key = Mem0Map.NewEntryKey();
		// infer=true: distillation deferred (verbatim like infer=false). Seam for later.
		var input = new MemoryEntryInput
		{
			Key = key,
			Version = 0,
			Type = "Project",
			Body = body,
			Tags = Mem0Map.ScopeTags(agent_id, run_id),
			Metadata = Mem0Map.MetadataToString(metadata),
		};
		await memory.UpsertAsync(project, store, [input], [], 0, ct);
		return (object)new { results = new[] { new { id = Mem0Map.MakeId(store, key), memory = body, @event = "ADD" } } };
	});

	[McpServerTool(Name = "search_memories", Title = "Search memories (mem0-compatible)", ReadOnly = true)]
	[Description("mem0-compatible FTS search within a `user_id` scope (optionally narrowed by `agent_id`/`run_id` tags and a `filters` object of metadata key/values). `score` is relevance order (not a calibrated distance). `projectKey` optional. Requires memory:read.")]
	public static async Task<object> SearchMemoriesAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null, string? query = null,
		string? user_id = null, string? agent_id = null, string? run_id = null,
		JsonElement? filters = null, int? limit = null,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

		var store = Mem0Map.StoreFromUserId(user_id);
		if (!await memory.StoreExistsAsync(project, store, ct))
			return (object)new { results = Array.Empty<object>() };

		var hits = (await memory.SearchAsync(project, store, query, null, ct))
			.Where(v => Mem0Map.TagsMatchScope(v.Tags, agent_id, run_id))
			.Where(v => Mem0Map.MatchesFilters(v.Metadata, filters))
			.ToList();
		IEnumerable<object> results = hits.Select((v, i) => Mem0Map.ToMem0Memory(v, store, Mem0Map.PositionScore(i, hits.Count)));
		if (limit is > 0) results = results.Take(limit.Value);
		return (object)new { results = results.ToList() };
	});

	[McpServerTool(Name = "get_memories", Title = "List memories by scope (mem0-compatible)", ReadOnly = true)]
	[Description("mem0-compatible. List all memories for a `user_id` scope (optionally narrowed by `agent_id`/`run_id` and a `filters` object of metadata key/values). `projectKey` optional. Requires memory:read.")]
	public static async Task<object> GetMemoriesAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null, string? user_id = null, string? agent_id = null, string? run_id = null,
		JsonElement? filters = null, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);

		var store = Mem0Map.StoreFromUserId(user_id);
		if (!await memory.StoreExistsAsync(project, store, ct))
			return (object)new { results = Array.Empty<object>() };

		var results = (await memory.ListAsync(project, store, null, ct))
			.Where(v => Mem0Map.TagsMatchScope(v.Tags, agent_id, run_id))
			.Where(v => Mem0Map.MatchesFilters(v.Metadata, filters))
			.Select(v => Mem0Map.ToMem0Memory(v, store, null))
			.ToList();
		return (object)new { results };
	});

	[McpServerTool(Name = "get_memory", Title = "Get one memory by id (mem0-compatible)", ReadOnly = true)]
	[Description("mem0-compatible. Fetch a single memory by its id. `projectKey` optional. Requires memory:read.")]
	public static async Task<object> GetMemoryAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null, string? id = null, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		if (string.IsNullOrWhiteSpace(id) || !Mem0Map.TryDecodeId(id, out var store, out var key))
			throw new ArgumentException($"invalid memory id '{id}'");

		if (await memory.StoreExistsAsync(project, store, ct))
		{
			var v = await memory.GetAsync(project, store, key, ct);
			if (v is not null) return Mem0Map.ToMem0Memory(v, store, null);
		}
		return (object)new { error = new { type = "NotFound", message = $"memory '{id}' not found" } };
	});

	[McpServerTool(Name = "update_memory", Title = "Update a memory (mem0-compatible)")]
	[Description("mem0-compatible. Replace a memory's text and/or metadata by id (type/tags preserved). `projectKey` optional. Requires memory:write.")]
	public static async Task<object> UpdateMemoryAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null, string? id = null, string? text = null, JsonElement? metadata = null,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		if (string.IsNullOrWhiteSpace(id) || !Mem0Map.TryDecodeId(id, out var store, out var key))
			throw new ArgumentException($"invalid memory id '{id}'");

		var cur = await memory.StoreExistsAsync(project, store, ct)
			? await memory.GetAsync(project, store, key, ct)
			: null;
		if (cur is null)
			return (object)new { error = new { type = "NotFound", message = $"memory '{id}' not found" } };

		var input = new MemoryEntryInput
		{
			Key = key,
			Version = cur.Version,
			Type = cur.Type,
			Description = cur.Description,
			Body = text ?? cur.Body,
			Tags = cur.Tags,
			Metadata = Mem0Map.MetadataToString(metadata) ?? cur.Metadata,
		};
		var outcome = await memory.UpsertAsync(project, store, [input], [], 0, ct);
		if (outcome.Result.Conflicts.Count > 0)
		{
			var c = outcome.Result.Conflicts[0];
			return (object)new { error = new { type = "Conflict", message = $"memory '{id}' changed concurrently ({c.Kind})" } };
		}
		return (object)new { id, @event = "UPDATE" };
	});

	[McpServerTool(Name = "delete_memory", Title = "Delete a memory (mem0-compatible)", Destructive = true)]
	[Description("mem0-compatible. Delete a memory by id (idempotent). `projectKey` optional. Requires memory:write.")]
	public static async Task<object> DeleteMemoryAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null, string? id = null, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		if (string.IsNullOrWhiteSpace(id) || !Mem0Map.TryDecodeId(id, out var store, out var key))
			throw new ArgumentException($"invalid memory id '{id}'");

		// Only touch the store if it exists — avoid auto-vivifying an empty store on a
		// delete of an unknown id. Delete is idempotent regardless.
		if (await memory.StoreExistsAsync(project, store, ct))
			await memory.UpsertAsync(project, store, [], [new MemoryDelete(key, 0)], 0, ct);
		return (object)new { id, @event = "DELETE" };
	});

	[McpServerTool(Name = "delete_all_memories", Title = "Delete memories by scope (mem0-compatible)", Destructive = true)]
	[Description("mem0-compatible. Delete all memories matching a `user_id` scope (optionally narrowed by `agent_id`/`run_id`). Scoped delete, NOT a store drop. `projectKey` optional. Requires memory:write.")]
	public static async Task<object> DeleteAllMemoriesAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string? projectKey = null, string? user_id = null, string? agent_id = null, string? run_id = null,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		var project = ModuleMcp.ResolveProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);

		var store = Mem0Map.StoreFromUserId(user_id);
		if (!await memory.StoreExistsAsync(project, store, ct))
			return (object)new { deleted_count = 0 };

		var keys = (await memory.ListAsync(project, store, null, ct))
			.Where(v => Mem0Map.TagsMatchScope(v.Tags, agent_id, run_id))
			.Select(v => v.Key)
			.ToList();
		if (keys.Count > 0)
			await memory.UpsertAsync(project, store, [], keys.Select(k => new MemoryDelete(k, 0)).ToList(), 0, ct);
		return (object)new { deleted_count = keys.Count };
	});
}
