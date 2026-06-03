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

	// ---- ergonomic verbs: remember / recall (thin over the structural tools) ----
	//
	// Two low-ceremony verbs for the agent's own working memory, layered on top of the
	// structural store/upsert/search tools. They add ONE thing the structural surface
	// lacks: a `scope` dimension over the per-project store files —
	//   project   → the key's own project  (default; the usual case)
	//   workspace → reserved "$workspace" container (cross-project shared memory)
	// `recall` with no scope CASCADES both (project ⊕ workspace) and returns hits
	// labelled by scope so precedence is visible (project is most specific → listed
	// first). The "$workspace" container is a plain memory store file under a seeded
	// built-in project (M028); it is shared across projects by design, so any caller
	// holding the memory scope may reach it. Per-workspace isolation is future work
	// (idea workspace-memory). Personal facts are carried by type=User, not a separate
	// container. Curated/temporal writes still go through memory.upsert.

	const string WorkspaceContainer = "$workspace";
	const string DefaultStore = "notes";

	// Stores skipped by the default "search every store" recall: sensitive operational
	// stores that must never be auto-pulled into an agent's context (e.g. "ops" has held
	// secrets). An explicit `store:"ops"` recall still reaches it — only the implicit
	// all-stores sweep excludes it.
	static readonly HashSet<string> RecallExcludedStores = new(StringComparer.OrdinalIgnoreCase) { "ops" };

	[McpServerTool(Name = "memory.remember", Title = "Remember a fact")]
	[Description("""
		Capture one durable fact, verbatim. The low-ceremony way to store a learning.
		`text` (required) is the fact. `scope` picks the container: project (default —
		the key's project) | workspace (cross-project shared). `store` groups entries
		within a scope (default "notes"). `type` is the taxonomy
		(User|Feedback|Project|Reference; default Project) — pick explicitly, no inference.
		`tags` is free CSV; `description` an optional one-line summary. A unique key is
		generated. Store durable facts not derivable from code/git/config; actionable work
		goes to a task board. Requires memory:write. Returns { id, scope, store, key }.
		""")]
	public static async Task<object> RememberAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string text, string? scope = null, string? projectKey = null, string? store = null,
		string? type = null, string? tags = null, string? description = null,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
		var container = ResolveScope(http, projectKey, scope);
		var st = NormalizeStore(store);
		var key = "m-" + Guid.NewGuid().ToString("N");
		var input = new MemoryEntryInput
		{
			Key = key,
			Version = 0,
			Type = string.IsNullOrWhiteSpace(type) ? "Project" : type,
			Description = description,
			Body = text,
			Tags = tags,
		};
		await memory.UpsertAsync(container.Key, st, [input], [], 0, ct);
		return (object)new { id = $"{container.Key}/{st}/{key}", scope = container.Scope, store = st, key };
	});

	[McpServerTool(Name = "memory.recall", Title = "Recall facts", ReadOnly = true)]
	[Description("""
		Full-text recall over your memory. The low-ceremony way to surface relevant facts.
		`query` (required) is matched by token (prefix), so paraphrases hit; search a few
		words you are confident appear (tokens are ANDed). `scope` narrows the search:
		omit it to CASCADE project ⊕ workspace (results labelled by scope, project
		first); or pass project | workspace for one. `store` narrows to a single
		store within each scope (default: search every store). Optional `type` filter
		(User|Feedback|Project|Reference) and `limit` (default 20). Requires memory:read.
		Returns { results: [{ scope, store, key, type, description, body, tags }] }.
		""")]
	public static async Task<object> RecallAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string query, string? scope = null, string? projectKey = null, string? store = null,
		string? type = null, int limit = 20, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");

		var results = new List<object>();
		foreach (var (scopeName, container) in RecallContainers(http, projectKey, scope))
		{
			// Which stores to search in this container: the named one, or all of them
			// except the sensitive ones (the implicit sweep must not leak ops/secrets).
			IReadOnlyList<string> stores = string.IsNullOrWhiteSpace(store)
				? (await memory.ListStoresAsync(container, ct)).Select(s => s.Name).Where(n => !RecallExcludedStores.Contains(n)).ToList()
				: [store!.Trim()];
			foreach (var st in stores)
			{
				if (!await memory.StoreExistsAsync(container, st, ct)) continue;
				foreach (var v in await memory.SearchAsync(container, st, query, type, ct))
				{
					results.Add(new { scope = scopeName, store = st, key = v.Key, type = v.Type, description = v.Description, body = v.Body, tags = v.Tags });
					if (results.Count >= limit) return (object)new { results };
				}
			}
		}
		return (object)new { results };
	});

	// Resolve a single explicit scope to its container projectKey (for remember).
	// project → the key's project (authorized via the claim); workspace → the reserved
	// shared container (gated only by the memory scope already asserted).
	static (string Scope, string Key) ResolveScope(IHttpContextAccessor http, string? projectKey, string? scope) =>
		(scope?.Trim().ToLowerInvariant()) switch
		{
			null or "" or "project" => ("project", ModuleMcp.ResolveProject(http, projectKey)),
			"workspace" => ("workspace", WorkspaceContainer),
			var s => throw new ArgumentException($"invalid scope '{s}' (project|workspace)"),
		};

	// The ordered list of (scope, container) to search for recall. A single scope → that
	// container; no scope → the full cascade, project first (most specific). The project
	// container is best-effort: a cross-project ("*") key with no projectKey can't resolve
	// a single project, so that leg is skipped rather than failing the whole recall.
	static List<(string Scope, string Key)> RecallContainers(IHttpContextAccessor http, string? projectKey, string? scope)
	{
		var s = scope?.Trim().ToLowerInvariant();
		if (!string.IsNullOrEmpty(s) && s != "all" && s != "cascade")
			return [ResolveScope(http, projectKey, s)];
		var list = new List<(string, string)>();
		try { list.Add(("project", ModuleMcp.ResolveProject(http, projectKey))); }
		catch (ArgumentException) { /* "*" key without an explicit projectKey — skip the project leg */ }
		list.Add(("workspace", WorkspaceContainer));
		return list;
	}

	static string NormalizeStore(string? store) =>
		string.IsNullOrWhiteSpace(store) ? DefaultStore : store.Trim();

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
		metadata = e.Metadata,
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
				Metadata = ModuleMcp.OptStr(e, "metadata"),
				PrevKey = ModuleMcp.OptStr(e, "prevKey"),
			});
		}
		return (upserts, deletes);
	}
}
