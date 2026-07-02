using System.ComponentModel;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Features;
using PetBox.Core.Search;
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
		Returns the pure write-ack { applied, currentVersion, inserted, closed, conflicts[],
		added[], updated[], removed[] }: added/updated/removed cover ONLY this call's entries
		(never other writers' history — there is no cursor parameter on a write) and carry
		key/type/description/version but NOT `body` by default (pass bodyLen > 0 for a sliced
		body). `currentVersion` is the store-wide cursor: for a full delta since a cursor,
		call memory.delta with it as `sinceVersion`.
		""")]
	public static async Task<MemoryUpsertResultView> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory,
		string projectKey, string store,
		[Description("Array of entry objects: { key, type, description, body, tags?, metadata?, version?, prevKey? }, or { key, deleted:true } to soft-delete.")] MemoryEntryInputDto[] entries,
		[Description("Slice length (chars) of each echoed entry body; 0 (default) = no body (compact echo). \"…\" appended when cut.")] int bodyLen = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var (upserts, deletes) = ParseEntries(entries);
		return Serialize(await memory.UpsertAsync(projectKey, store, upserts, deletes, ct), bodyLen);
	}

	[McpServerTool(Name = "memory.delta", Title = "Memory delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryUpsertResultView))]
	[Description("Return entries added/updated/removed since `sinceVersion` (no writes) — THE cursor/catch-up surface (a memory.upsert ack echoes only its own call; pass its `currentVersion` here for the full store delta). Bodies omitted unless bodyLen > 0 (compact by default). Requires memory:read.")]
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

	// ---- read + capture verbs: search / remember ----
	//
	// memory.search is THE read verb (spec uniform-entity-verbs v2: list = search without
	// a query; it replaced the former memory.list + memory.recall pair) and memory.remember
	// the low-ceremony capture verb. Both carry the `scope` dimension over the per-project
	// store files —
	//   project   → the key's own project  (default; the usual case)
	//   workspace → the shared cross-project container ("$system") — facts that span
	//               projects or are about the user live here, one place for everyone.
	// `search` with no scope CASCADES both (project ⊕ workspace) and returns rows labelled
	// by scope so precedence is visible (project first); when the key's project IS the
	// shared container the two collapse and it's searched once. Any memory-scoped key may
	// reach the shared container (that's the point). Personal facts are carried by
	// type=User, not a separate container. Curated/temporal writes go through memory.upsert.

	const string WorkspaceContainer = "$system";
	const string DefaultStore = "notes";

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
		await memory.UpsertAsync(container.Key, st, [input], [], ct);
		return new MemoryRememberResult($"{container.Key}/{st}/{key}", container.Scope, st, key);
	}

	[McpServerTool(Name = "memory.search", Title = "Read memory entries (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemorySearchResultView))]
	[Description("""
		THE memory read verb — one tool for both LISTING and SEARCH (list = search without
		`q`; replaces the former memory.list + memory.recall).

		MODES. Without `q`: a DETERMINISTIC listing of the active entries in scope; default
		order `updated` desc (the freshest fact first — keys are opaque generated ids).
		With `q`: a RELEVANCE selection (hybrid lexical FTS5 token/prefix ⊕ semantic
		vectors, RRF-fused; search a few words you are confident appear — lexical tokens
		are ANDed, the semantic leg catches paraphrases; semantic is silently absent when
		no embedding is configured); default order relevance, and the response carries
		`retrievers` { lexical, semantic, degraded }.

		SCOPE (both modes): omit `scope` to CASCADE project ⊕ workspace (rows labelled by
		scope, project first); or pass project | workspace for one. `store` narrows to a
		single store within each scope; by default EVERY store is swept except the
		sensitive ones (e.g. "ops" — an explicit store:"ops" still reaches it). Optional
		`type` filter (User|Feedback|Project|Reference) — a predicate in both modes.

		SORT: `sort` = {by: relevance|created|updated, desc?}. Without `q` the default is
		updated desc (asking for relevance is an error); with `q` the default is relevance,
		and an explicit created/updated sort reorders WITHIN the relevance-selected set
		(`desc` is ignored for relevance). `limit` caps the rows (default 20 in both modes;
		0 = no cap — a listing is then bounded only by the output budget, a query by its
		candidate pool). Bodies are full by default; pass `bodyLen` > 0 for a snippet
		(description + first N chars), then pull a full body with memory.get.

		`includeUsage` adds surfaced/opened/lastHitAt counters per row. A search answer
		counts an impression for the returned rows; a listing (no `q`) is curation and
		counts nothing. The response has a HARD OUTPUT BUDGET (~30k serialized chars):
		overflowing rows are prefix-cut in result order and flagged `truncated:true` +
		`omitted` + a narrowing `hint`; no markers = the complete answer. Requires
		memory:read.

		Returns { items: [{ scope, store, key, type, description, body, tags, version }], retrievers? };
		`version` is the entry's CAS baseline for memory.upsert (pass it back to edit without a Stale round-trip).
		""")]
	public static async Task<MemorySearchResultView> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, IMemoryService memory, IMemoryUsageRecorder usage,
		[Description("Search query. Omit for a deterministic listing (list = search without q).")] string? q = null,
		[Description("project | workspace; omit to cascade both (rows labelled by scope, project first).")] string? scope = null,
		string? projectKey = null,
		[Description("Narrow to one store within each scope (default: sweep every store except the sensitive ones).")] string? store = null,
		[Description("Taxonomy filter: User|Feedback|Project|Reference.")] string? type = null,
		[Description("Sort order: {by: relevance|created|updated, desc?}. Default: updated desc (listing) / relevance (with q).")] SortInput? sort = null,
		[Description("Max rows returned (default 20; 0 = no cap — the output budget still applies).")] int? limit = null,
		[Description("Snippet length (chars) per row body; 0 (default) = full body. \"…\" appended when cut.")] int bodyLen = 0,
		[Description("Include usage counters (surfaced/opened/lastHitAt) per row (default false).")] bool includeUsage = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var hasQuery = !string.IsNullOrWhiteSpace(q);
		var cap = limit ?? DefaultLimit;

		var rows = new List<MemorySearchHitView>();
		// Aggregate provenance across every scope searched: lexical/semantic = OR of the
		// legs that ran; degraded = OR (any leg that wanted semantic but couldn't).
		SearchRetrievers? retrievers = null;
		foreach (var (scopeName, container) in SearchContainers(http, projectKey, scope))
		{
			ct.ThrowIfCancellationRequested();
			var remaining = cap == 0 ? 0 : cap - rows.Count;
			if (cap > 0 && remaining <= 0) break;
			var res = await memory.SearchEntriesAsync(container, new SearchRequest<MemoryEntryFilter, MemorySortBy>
			{
				Query = hasQuery ? q : null,
				Filter = new MemoryEntryFilter(store, type),
				Sort = ParseSort(sort),
				Limit = remaining,
				BodyLen = bodyLen,
			}, ct);
			if (res.Retrievers is { } r)
				retrievers = retrievers is { } agg
					? new SearchRetrievers(agg.Lexical | r.Lexical, agg.Semantic | r.Semantic, agg.Degraded | r.Degraded)
					: r;

			// Usage counters are keyed per (store, key) — rows may span stores in one container.
			var usageMap = new Dictionary<string, MemoryUsageView>(StringComparer.Ordinal);
			if (includeUsage && res.Hits.Count > 0)
				foreach (var g in res.Hits.GroupBy(h => h.Store, StringComparer.OrdinalIgnoreCase))
					foreach (var kv in await memory.GetUsageAsync(container, g.Key, g.Select(h => h.Entry.Key).ToList(), ct))
						usageMap[g.Key + "\x1f" + kv.Key] = kv.Value;
			foreach (var h in res.Hits)
			{
				var u = includeUsage && usageMap.TryGetValue(h.Store + "\x1f" + h.Entry.Key, out var uv) ? uv : null;
				rows.Add(new MemorySearchHitView(scopeName, h.Store, h.Entry.Key, h.Entry.Type, h.Entry.Description,
					h.Entry.Body, h.Entry.Tags, h.Entry.Version,
					includeUsage ? (u?.Surfaced ?? 0) : null, includeUsage ? (u?.Opened ?? 0) : null, u?.LastHitAt));
			}
			// Impression = the rows a SEARCH answer returned (a listing is curation — not counted).
			if (hasQuery)
				foreach (var g in res.Hits.GroupBy(h => h.Store, StringComparer.OrdinalIgnoreCase))
					usage.Surfaced(container, g.Key, g.Select(h => h.Entry.Key).ToList());
		}

		// Response budget (MCP-adapter-only): measured on the wire form of the rows as they
		// will be sent (bodies already sliced by the service), prefix-cut, marked — never silent.
		var (kept, omitted) = new ResponseBudget().Take(rows);
		return new MemorySearchResultView(
			kept,
			Retrievers: retrievers is { } fin ? new RetrieverInfo(fin.Lexical, fin.Semantic, fin.Degraded) : null,
			Truncated: omitted > 0 ? true : null,
			Omitted: omitted > 0 ? omitted : null,
			Hint: omitted > 0 ? SearchBudgetHint : null);
	}

	// The bounded default of memory.search (both modes; spec bounded-result-sets).
	const int DefaultLimit = 20;

	// Surfaced on MemorySearchResultView.Hint when the rows were cut by the response budget.
	const string SearchBudgetHint =
		"Output budget exceeded: entries were truncated (see truncated/omitted). Narrow the " +
		"read: `scope`/`store` (one container/store), `type` (one taxonomy), a lower `limit`, " +
		"`bodyLen` (snippet bodies), or memory.get for one entry's full body.";

	// Map the wire `sort` argument onto the service sort axis; an unknown axis is a clear error.
	static (MemorySortBy By, bool Desc)? ParseSort(SortInput? sort)
	{
		if (sort is null || string.IsNullOrWhiteSpace(sort.By)) return null;
		if (!Enum.TryParse<MemorySortBy>(sort.By.Trim(), ignoreCase: true, out var by))
			throw new ArgumentException($"sort.by '{sort.By}' is not a sort axis (valid: relevance|created|updated)");
		return (by, sort.Desc);
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

	// The ordered list of (scope, container) memory.search reads. A single scope → that
	// container; no scope → the full cascade, project first (most specific). The project
	// container is best-effort: a cross-project ("*") key with no projectKey can't resolve
	// a single project, so that leg is skipped rather than failing the whole read.
	static List<(string Scope, string Key)> SearchContainers(IHttpContextAccessor http, string? projectKey, string? scope)
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
