using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for the Memory module: named store lifecycle + temporal entry content.
// A THIN adapter — it asserts the scope/feature/project guards, parses the JSON entry
// payload, and delegates every domain decision (taxonomy, tags, FTS, temporal write)
// to IMemoryService. It must not touch the store or DB context directly (a NetArchTest
// enforces this). v1 is project-scoped. Scopes: memory:read / memory:write.
[McpServerToolType]
public static class MemoryTools
{
	[McpServerTool(Name = "memory.store_create", Title = "Create a memory store", UseStructuredContent = true, OutputSchemaType = typeof(MemoryStoreCreatedResult))]
	[Description("CREATE a named memory store in a project (fails if it already exists). Requires memory:write.")]
	public static async Task<MemoryStoreCreatedResult> StoreCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, string? description = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var meta = await memory.CreateStoreAsync(projectKey, store, description, ct);
		return new MemoryStoreCreatedResult(meta.ProjectKey, meta.Name, meta.Description, meta.CreatedAt);
	}

	[McpServerTool(Name = "memory.store_list", Title = "List memory stores", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryStoreListResult))]
	[Description("List memory stores in a project. Requires memory:read.")]
	public static async Task<MemoryStoreListResult> StoreListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var list = await memory.ListStoresAsync(projectKey, ct);
		return new MemoryStoreListResult(list.Select(s => new MemoryStoreRow(s.Name, s.Description, s.CreatedAt)).ToList());
	}

	[McpServerTool(Name = "memory.store_delete", Title = "Delete a memory store", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryStoreDeletedResult))]
	[Description("Delete a memory store and its entries. Requires memory:write.")]
	public static async Task<MemoryStoreDeletedResult> StoreDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		return new MemoryStoreDeletedResult(await memory.DeleteStoreAsync(projectKey, store, ct));
	}

	[McpServerTool(Name = "memory.list", Title = "List memory entries", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryListResult))]
	[Description("List active entries of a memory store, ordered by key. Optional `type` filter (User|Feedback|Project|Reference). Bounded by `limit` (default 20; 0 = no limit) and bodies are full unless `bodyLen` > 0 (snippet). `includeUsage` adds surfaced/opened/lastHitAt counters per entry (listing itself is NOT counted as usage). The response has a HARD OUTPUT BUDGET (~30k serialized chars): when the rows no longer fit they are prefix-cut in key order and flagged with `truncated:true` + `omitted` (rows dropped) plus a `hint` on how to narrow (`type`, `limit`, `bodyLen`, or memory.get for one entry); no markers = the complete list. Requires memory:read.")]
	public static async Task<MemoryListResult> ListAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, string? type = null,
		[Description("Snippet length (chars) per entry body; 0 (default) = full body. \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Max entries returned (default 20; 0 = no limit).")] int limit = 20,
		[Description("Include usage counters (surfaced/opened/lastHitAt) per entry (default false).")] bool includeUsage = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		// A bulk listing is curation, not usage — deliberately not recorded.
		var rows = Project(await memory.ListAsync(projectKey, store, type, ct), bodyLen, limit);
		if (includeUsage)
			rows = await WithUsageAsync(memory, projectKey, store, rows, ct);
		// Response budget (spec bounded-result-sets): measured on the wire form of the rows,
		// prefix-cut in key order, marked structurally — never silent. An in-budget list
		// serializes byte-identical to the unbudgeted shape (the markers stay null).
		var (kept, omitted) = new ResponseBudget().Take(rows);
		return omitted == 0
			? new MemoryListResult(rows)
			: new MemoryListResult(kept, Truncated: true, Omitted: omitted, Hint: ListBudgetHint);
	}

	// Surfaced on MemoryListResult.Hint when the rows were cut by the response budget.
	const string ListBudgetHint =
		"Output budget exceeded: entries were truncated (see truncated/omitted). Narrow the " +
		"query: `type` (one taxonomy), a lower `limit`, `bodyLen` (snippet bodies), or " +
		"memory.get for one entry's full body.";

	[McpServerTool(Name = "memory.get", Title = "Get a memory entry", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryEntryView))]
	[Description("Get the active entry by key, or null. Requires memory:read.")]
	public static async Task<MemoryEntryView?> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory, IMemoryUsageRecorder usage,
		string projectKey, string store, string key, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var entry = await memory.GetAsync(projectKey, store, key, ct);
		if (entry is not null)
			usage.Opened(projectKey, store, key); // engagement: the entry was deliberately opened
		return entry;
	}

	// memory.search is GONE (verb dedup, spec surface-economy): it was a strict subset of
	// memory.recall — the same IMemoryService.SearchAsync hybrid underneath, minus recall's
	// scope cascade, all-store sweep and per-hit CAS version. Every search parameter
	// (type/bodyLen/limit/lexical/semantic/includeUsage) exists on recall; the single-store
	// scenario is recall with scope:"project" + store:"<name>".

	[McpServerTool(Name = "memory.upsert", Title = "Upsert memory entries", UseStructuredContent = true, OutputSchemaType = typeof(MemoryUpsertResultView))]
	[Description("""
		PATCH per entry (declarative temporal upsert into a store). Requires memory:write.
		On an EDIT (version > 0) an omitted field stays UNCHANGED — send only what you change;
		to clear a field pass it explicitly empty (description/body/metadata: "", tags: "").
		On a NEW entry (version 0) omitted fields start empty.
		`entries` is a JSON array of { key, type, description, body, tags?, version?, prevKey? }.
		`type` (required) is the taxonomy: User (about the user) | Feedback (a correction/
		preference on how to work) | Project (durable project fact/constraint) | Reference
		(pointer to an external resource). Pick one. `tags` is free CSV, normalised on write.
		`version` is the baseline you last saw (0 = new). Set `prevKey` to rename.
		To delete an entry, pass { key, deleted:true } (optional version baseline) — it is
		soft-closed (history kept) and appears in the result's `removed`.
		Store durable facts not derivable from code/git/config; actionable work goes to a
		task board, not here.
		Result: { applied, currentVersion, inserted, closed, conflicts[], added[], updated[], removed[] };
		added/updated carry key/type/description/version but NOT `body` by default — the echo
		is a compact cursor-advance (pass bodyLen > 0 for a sliced body). CURSOR CONTRACT: pass
		the prior response's `currentVersion` as the next `sinceVersion` (0 echoes every entry,
		bodiless); a single entry's `version` is smaller and re-echoes the whole recent delta.
		""")]
	public static async Task<MemoryUpsertResultView> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store,
		[Description("Array of entry objects: { key, type, description, body, tags?, metadata?, version?, prevKey? }, or { key, deleted:true } to soft-delete.")] MemoryEntryInputDto[] entries,
		[Description("Cursor: pass the prior response's `currentVersion` so the echo is just your delta. 0 (default) echoes every entry (bodiless).")] long sinceVersion = 0,
		[Description("Slice length (chars) of each echoed entry body; 0 (default) = no body (compact echo). \"…\" appended when cut.")] int bodyLen = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var (upserts, deletes) = ParseEntries(entries);
		return Serialize(await memory.UpsertAsync(projectKey, store, upserts, deletes, sinceVersion, ct), bodyLen);
	}

	[McpServerTool(Name = "memory.delta", Title = "Memory delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryUpsertResultView))]
	[Description("Return entries added/updated/removed since `sinceVersion` (no writes); bodies omitted unless bodyLen > 0 (compact by default). Requires memory:read.")]
	public static async Task<MemoryUpsertResultView> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store, long sinceVersion,
		[Description("Slice length (chars) of each entry body; 0 (default) = no body (compact). \"…\" appended when cut.")] int bodyLen = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		return Serialize(await memory.DeltaAsync(projectKey, store, sinceVersion, ct), bodyLen);
	}

	// ---- ergonomic verbs: remember / recall (thin over the structural tools) ----
	//
	// Two low-ceremony verbs for the agent's own working memory, layered on top of the
	// structural store/upsert tools (recall IS the search verb — the old memory.search
	// was a strict subset and is gone). They add ONE thing the structural surface
	// lacks: a `scope` dimension over the per-project store files —
	//   project   → the key's own project  (default; the usual case)
	//   workspace → the shared cross-project container ("$system") — facts that span
	//               projects or are about the user live here, one place for everyone.
	// `recall` with no scope CASCADES both (project ⊕ workspace) and returns hits labelled
	// by scope so precedence is visible (project first); when the key's project IS the
	// shared container the two collapse and it's searched once. Any memory-scoped key may
	// reach the shared container (that's the point). Personal facts are carried by
	// type=User, not a separate container. Curated/temporal writes go through memory.upsert.

	const string WorkspaceContainer = "$system";
	const string DefaultStore = "notes";

	// Stores skipped by the default "search every store" recall: sensitive operational
	// stores that must never be auto-pulled into an agent's context (e.g. "ops" has held
	// secrets). An explicit `store:"ops"` recall still reaches it — only the implicit
	// all-stores sweep excludes it.
	static readonly HashSet<string> RecallExcludedStores = new(StringComparer.OrdinalIgnoreCase) { "ops" };

	[McpServerTool(Name = "memory.remember", Title = "Remember a fact", UseStructuredContent = true, OutputSchemaType = typeof(MemoryRememberResult))]
	[Description("""
		CREATE one durable fact, verbatim (always a new entry; edits go via memory.upsert).
		The low-ceremony way to store a learning.
		`text` (required) is the fact. `scope` picks the container: project (default —
		the key's project) | workspace (cross-project shared). `store` groups entries
		within a scope (default "notes"). `type` is the taxonomy
		(User|Feedback|Project|Reference; default Project) — pick explicitly, no inference.
		`tags` is free CSV; `description` an optional one-line summary. A unique key is
		generated. Store durable facts not derivable from code/git/config; actionable work
		goes to a task board. Requires memory:write. Returns { id, scope, store, key }.
		""")]
	public static async Task<MemoryRememberResult> RememberAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string text, string? scope = null, string? projectKey = null, string? store = null,
		string? type = null, string? tags = null, string? description = null,
		CancellationToken ct = default)
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
		return new MemoryRememberResult($"{container.Key}/{st}/{key}", container.Scope, st, key);
	}

	[McpServerTool(Name = "memory.recall", Title = "Recall facts", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryRecallResult))]
	[Description("""
		THE memory search verb: hybrid recall (lexical FTS5 token/prefix ⊕ semantic vectors,
		RRF-fused) over your memory, ranked by relevance.
		`query` (required) — search a few words you are confident appear (lexical tokens are
		ANDed; the semantic leg catches paraphrases). `scope` narrows the search:
		omit it to CASCADE project ⊕ workspace (results labelled by scope, project
		first); or pass project | workspace for one. `store` narrows to a single
		store within each scope (default: search every store). Optional `type` filter
		(User|Feedback|Project|Reference) and `limit` (default 20; the answer is always
		bounded). `lexical`/`semantic`
		(default both on) toggle each retriever; semantic is silently
		off when embedding is unavailable. Bodies are full by
		default; pass `bodyLen` > 0 for a snippet (description + first N chars), then pull a
		full body with memory.get. Requires memory:read.
		Returns { results: [{ scope, store, key, type, description, body, tags, version }], retrievers: { lexical, semantic, degraded } };
		`version` is the entry's CAS baseline for memory.upsert (pass it back to edit without a Stale round-trip).
		""")]
	public static async Task<MemoryRecallResult> RecallAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory, IMemoryUsageRecorder usage,
		string query, string? scope = null, string? projectKey = null, string? store = null,
		string? type = null, int limit = 20,
		[Description("Snippet length (chars) per result body; 0 (default) = full body. \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Run the lexical FTS retriever (default true).")] bool? lexical = null,
		[Description("Run the semantic vector retriever (default true; no-op when embedding is unavailable).")] bool? semantic = null,
		[Description("Include usage counters (surfaced/opened/lastHitAt) per hit (default false).")] bool includeUsage = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");
		if (limit <= 0) limit = 20; // a recall answer is always bounded (spec bounded-result-sets)

		var results = new List<MemoryRecallHit>();
		// Aggregate provenance across every scope/store searched: lexical/semantic = OR of
		// the legs that ran; degraded = OR (any leg that wanted semantic but couldn't).
		var aggLexical = false;
		var aggSemantic = false;
		var aggDegraded = false;
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
				var res = await memory.SearchAsync(container, st, query, type, lexical, semantic, ct);
				aggLexical |= res.Retrievers.Lexical;
				aggSemantic |= res.Retrievers.Semantic;
				aggDegraded |= res.Retrievers.Degraded;
				var added = new List<string>();
				var full = false;
				IReadOnlyDictionary<string, MemoryUsageView>? usageMap = includeUsage && res.Hits.Count > 0
					? await memory.GetUsageAsync(container, st, res.Hits.Select(h => h.Key).ToList(), ct)
					: null;
				foreach (var v in res.Hits)
				{
					var u = usageMap is not null && usageMap.TryGetValue(v.Key, out var uv) ? uv : null;
					results.Add(new MemoryRecallHit(scopeName, st, v.Key, v.Type, v.Description,
						ModuleMcp.SnippetBody(v.Body, bodyLen), v.Tags, v.Version,
						usageMap is null ? null : (u?.Surfaced ?? 0), usageMap is null ? null : (u?.Opened ?? 0), u?.LastHitAt));
					added.Add(v.Key);
					if (results.Count >= limit) { full = true; break; }
				}
				// Impression = the hits this answer RETURNED for this container/store.
				usage.Surfaced(container, st, added);
				if (full) return new MemoryRecallResult(results, new RetrieverInfo(aggLexical, aggSemantic, aggDegraded));
			}
		}
		return new MemoryRecallResult(results, new RetrieverInfo(aggLexical, aggSemantic, aggDegraded));
	}

	// Decorate already-projected rows with their usage counters (entries that never
	// surfaced have no row → zeros).
	static async Task<List<MemoryEntryRow>> WithUsageAsync(IMemoryService memory, string projectKey, string store,
		List<MemoryEntryRow> rows, CancellationToken ct)
	{
		if (rows.Count == 0) return rows;
		var map = await memory.GetUsageAsync(projectKey, store, rows.Select(r => r.Key).ToList(), ct);
		return rows.Select(r =>
		{
			var u = map.TryGetValue(r.Key, out var uv) ? uv : null;
			return r with { Surfaced = u?.Surfaced ?? 0, Opened = u?.Opened ?? 0, LastHitAt = u?.LastHitAt };
		}).ToList();
	}

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
		// Dedup: when the key's project IS the shared container, project and workspace
		// collapse to one — don't search the same container (and re-list hits) twice.
		if (!list.Any(c => c.Item2 == WorkspaceContainer))
			list.Add(("workspace", WorkspaceContainer));
		return list;
	}

	static string NormalizeStore(string? store) =>
		string.IsNullOrWhiteSpace(store) ? DefaultStore : store.Trim();

	// ---- adapter plumbing: JSON parsing + wire shaping (no domain logic) ----

	static MemoryUpsertResultView Serialize(MemoryUpsertOutcome o, int bodyLen = 0)
	{
		var r = o.Result;
		return new MemoryUpsertResultView(
			Applied: r.Applied,
			CurrentVersion: r.CurrentVersion,
			Inserted: r.Inserted,
			Closed: r.Closed,
			Conflicts: r.Conflicts.Select(c => new MemoryConflictView(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion)).ToList(),
			Added: r.Added.Select(e => EntryDto(e, bodyLen)).ToList(),
			Updated: r.Updated.Select(e => EntryDto(e, bodyLen)).ToList(),
			Removed: r.Removed.ToList());
	}

	// `body` is sliced to bodyLen (null when 0 → omitted by the serializer) so the write-echo
	// stays compact; `description` (a one-liner) is kept to orient the merge.
	static MemoryEntryRow EntryDto(MemoryEntry e, int bodyLen = 0) => new(
		Key: e.Key,
		Type: e.Type.ToString(),
		Description: e.Description,
		Body: ModuleMcp.SliceBody(e.Body, bodyLen),
		Tags: e.Tags,
		Version: e.Version,
		Metadata: e.Metadata);

	// Read-path projection (spec read-snippet-on-demand + bounded-result-sets): cap at `limit`
	// (0 = no cap) and snippet each body to `bodyLen` (0 = full, back-compat). description/tags
	// stay so a hit can be judged without the full body.
	static List<MemoryEntryRow> Project(IEnumerable<MemoryEntryView> entries, int bodyLen, int limit)
	{
		var capped = limit > 0 ? entries.Take(limit) : entries;
		return capped.Select(e => new MemoryEntryRow(
			Key: e.Key,
			Type: e.Type,
			Description: e.Description,
			Body: ModuleMcp.SnippetBody(e.Body, bodyLen),
			Tags: e.Tags,
			Version: e.Version,
			Metadata: e.Metadata)).ToList();
	}

	// Map the typed entry inputs into service inputs + soft-deletes. Taxonomy/tag normalization
	// happens in the service. `deleted:true` carries a soft-delete (only key + optional version);
	// otherwise key and type are required (as the old JsonElement parser enforced).
	static (List<MemoryEntryInput> Upserts, List<MemoryDelete> Deletes) ParseEntries(MemoryEntryInputDto[] entries)
	{
		var upserts = new List<MemoryEntryInput>();
		var deletes = new List<MemoryDelete>();
		foreach (var e in entries)
		{
			// `deleted:true` soft-deletes the entry (only key + optional version needed).
			if (e.Deleted)
			{
				deletes.Add(new MemoryDelete(Req(e.Key, "key"), e.Version));
				continue;
			}
			upserts.Add(new MemoryEntryInput
			{
				Key = Req(e.Key, "key"),
				Version = e.Version,
				Type = Req(e.Type, "type"),
				Description = e.Description,
				Body = e.Body,
				Tags = e.Tags,
				Metadata = e.Metadata,
				PrevKey = e.PrevKey,
			});
		}
		return (upserts, deletes);

		static string Req(string? v, string name) =>
			!string.IsNullOrWhiteSpace(v) ? v! : throw new ArgumentException($"{name} is required");
	}
}
