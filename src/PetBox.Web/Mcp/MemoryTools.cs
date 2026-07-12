using System.ComponentModel;
using LinqToDB;
using LinqToDB.Async;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
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
	[McpServerTool(Name = "memory_store_create", Title = "Create a memory store", UseStructuredContent = true, OutputSchemaType = typeof(MemoryStoreCreatedResult))]
	[Description("CREATE a named memory store. `scope`: project (default) | workspace. Requires memory:write.")]
	public static async Task<MemoryStoreCreatedResult> StoreCreateAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory,
		string projectKey, string store, string? description = null,
		[Description("project | workspace (default project).")] string? scope = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		projectKey = (await ResolveScopeAsync(http, db, projectKey, scope, ct)).Key;
		await AssertMemoryProjectAsync(http, db, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var meta = await memory.CreateStoreAsync(projectKey, store, description, ct);
		return new MemoryStoreCreatedResult(meta.ProjectKey, meta.Name, meta.Description, meta.CreatedAt);
	}

	[McpServerTool(Name = "memory_store_list", Title = "List memory stores", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryStoreListResult))]
	[Description("""
		List memory stores. `scope`: project (default) | workspace. Omit to CASCADE project
		⊕ workspace (rows labelled by scope, project first) — same as memory_search.
		`includeUsage` (default false) attaches a per-store usage aggregate: totalEntries,
		surfacedAtLeastOnce/openedAtLeastOnce (+ fractions over the active set), medianLastHitAt
		(the median last-hit of the surfaced entries — "recency"), and the dead tail
		(deadCount never-surfaced entries + the oldest-first sample deadTailKeys, prime pruning
		candidates). Reading this does NOT count as usage (curation, not an impression).
		""")]
	public static async Task<MemoryStoreListResult> StoreListAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory,
		string? projectKey = null,
		[Description("Attach a per-store usage aggregate (coverage, median recency, dead tail) (default false).")] bool includeUsage = false,
		[Description("project | workspace; omit to cascade both (rows labelled by scope, project first).")] string? scope = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var rows = new List<MemoryStoreRow>();
		foreach (var (scopeName, container) in await SearchContainersAsync(http, db, projectKey, scope, ct))
		{
			ct.ThrowIfCancellationRequested();
			try { await AssertMemoryProjectAsync(http, db, container, ct); }
			catch (UnauthorizedAccessException) { continue; }
			var list = await memory.ListStoresAsync(container, ct);
			foreach (var s in list)
			{
				MemoryStoreUsageRow? usage = null;
				if (includeUsage)
				{
					var a = await memory.GetUsageAggregateAsync(container, s.Name, ct: ct);
					usage = new MemoryStoreUsageRow(a.TotalEntries, a.SurfacedAtLeastOnce, a.OpenedAtLeastOnce,
						a.SurfacedFraction, a.OpenedFraction, a.MedianLastHitAt, a.DeadTail.Count, a.DeadTail.TopKeys);
				}
				rows.Add(new MemoryStoreRow(scopeName, s.Name, s.Description, s.CreatedAt, usage));
			}
		}
		return new MemoryStoreListResult(rows);
	}

	[McpServerTool(Name = "memory_store_delete", Title = "Delete a memory store", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryStoreDeletedResult))]
	[Description("Delete a memory store and its entries. `scope`: project (default) | workspace. Requires memory:write.")]
	public static async Task<MemoryStoreDeletedResult> StoreDeleteAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory,
		string projectKey, string store,
		[Description("project | workspace (default project).")] string? scope = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		projectKey = (await ResolveScopeAsync(http, db, projectKey, scope, ct)).Key;
		await AssertMemoryProjectAsync(http, db, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		return new MemoryStoreDeletedResult(await memory.DeleteStoreAsync(projectKey, store, ct));
	}

	[McpServerTool(Name = "memory_get", Title = "Get memory entries by key", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryGetResultView))]
	[Description("""
		Get the active entries by key — full bodies, addressed. `key` reads ONE; `keys` reads a
		BATCH in one call (the cheap path after a bodyLen:0 search: pull the 1-5 keys you actually
		need at once, not one round-trip each). Always returns { entries: [...] }, in the asked
		order.
		In a BATCH a key that matches nothing is silently dropped (soft filter, like tasks_search
		`keys`) and an empty result is not an error; with a single `key` a miss stays a not-found
		ERROR (never a bare null — strict MCP clients reject a null structured result; the error
		rides the isError channel).
		`scope`: project (default) | workspace. Omit to CASCADE project first, then workspace —
		the same cascade contract as memory_search. Requires memory:read.
		""")]
	public static async Task<MemoryGetResultView> GetAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory, IMemoryUsageRecorder usage,
		string projectKey, string store,
		[Description("One key to read. Combine with `keys` or use either alone.")] string? key = null,
		[Description("Batch of keys read in ONE call; a key that matches nothing is silently dropped (soft filter).")] string[]? keys = null,
		[Description("project | workspace; omit to cascade project first, then workspace.")] string? scope = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);

		// The ask: `key` ⊕ `keys`, de-duped, order preserved. A BATCH ask (any `keys` supplied)
		// tolerates misses; a lone `key` keeps the historic not-found error.
		var batch = keys is { Length: > 0 };
		var wanted = new[] { key }.Concat(keys ?? [])
			.Where(k => !string.IsNullOrWhiteSpace(k))
			.Select(k => k!.Trim())
			.Distinct(StringComparer.Ordinal)
			.ToList();
		if (wanted.Count == 0) throw new ArgumentException("key or keys is required");

		// Cascade: explicit scope → one container; no scope → project first, then the
		// caller's own workspace container (never a hardcoded global). Unauthorized
		// cascade legs are skipped so a foreign container never surfaces. A key found in the
		// nearer container is NOT re-read from the farther one (project precedence).
		var found = new Dictionary<string, MemoryEntryView>(StringComparer.Ordinal);
		// Where each entry was read from — a cascade answer may mix containers, and a delivery
		// event belongs in the file of the container that served it.
		var origin = new Dictionary<string, (string Container, string Scope)>(StringComparer.Ordinal);
		foreach (var (scopeName, container) in await SearchContainersAsync(http, db, projectKey, scope, ct))
		{
			ct.ThrowIfCancellationRequested();
			try { await AssertMemoryProjectAsync(http, db, container, ct); }
			catch (UnauthorizedAccessException) { continue; }
			var missing = wanted.Where(k => !found.ContainsKey(k)).ToList();
			if (missing.Count == 0) break;
			// A cascade leg that doesn't HAVE the store contributes nothing — it is not a failure of
			// the read (the store may live in the other container). Without this the batch's soft
			// filter could not exist: an unresolved key walks to the far leg, and a store-not-found
			// there would throw the whole call.
			if (!await memory.StoreExistsAsync(container, store, ct)) continue;
			foreach (var entry in await memory.GetManyAsync(container, store, missing, ct))
			{
				found[entry.Key] = entry;
				origin[entry.Key] = (container, scopeName);
				// An engagement per entry actually HANDED OVER — a batch of N bodies read is N
				// opens, not one (the counter measures entries, not calls).
				usage.Opened(container, store, entry.Key);
			}
		}

		if (found.Count == 0 && !batch)
			throw new InvalidOperationException($"memory entry '{wanted[0]}' not found in store '{store}' (scope: {(scope ?? "cascade project+workspace")})");

		var entries = wanted.Where(found.ContainsKey).Select(k => found[k]).ToList();

		// Delivery events (spec: usage-cost-and-fit-separate) — one per entry HANDED OVER, so a
		// batch of N bodies is N deliveries. A get sends the FULL body (deliveredChars ==
		// bodyChars) and is a perfect fit by construction — the caller named the key — so kRel = 1
		// with no fused score behind it. Rank is the entry's position in the answer.
		foreach (var g in entries.Select((e, i) => (Entry: e, Rank: i + 1, From: origin[e.Key]))
			.GroupBy(x => x.From.Container, StringComparer.OrdinalIgnoreCase))
			usage.Delivered(g.Key, [.. g.Select(x => new MemoryDeliveryEvent(
				Tool: "get", Scope: x.From.Scope, Store: store, Key: x.Entry.Key,
				DeliveredChars: x.Entry.Body.Length, BodyChars: x.Entry.Body.Length,
				RowChars: ResponseBudget.CostOf(x.Entry),
				Rank: x.Rank, ScoreRaw: null, KRel: 1, SessionId: McpSessionId(http),
				UsageSource: DeliberateSource))]);

		return new MemoryGetResultView(entries);
	}

	[McpServerTool(Name = "memory_upsert", Title = "Upsert memory entries", UseStructuredContent = true, OutputSchemaType = typeof(MemoryUpsertResultView))]
	[Description("""
		PATCH per entry (declarative temporal upsert into a store). Requires memory:write.
		On an EDIT (version > 0) an omitted field stays UNCHANGED — send only what you change;
		to clear a field pass it explicitly empty (description/body/metadata: "", tags: []).
		On a NEW entry (version 0) omitted fields start empty.
		`entries` is a JSON array of { key, type, description, body, tags?, version?, prevKey? }.
		`type` (required) is the taxonomy: User (about the user) | Feedback (a correction/
		preference on how to work) | Project (durable project fact/constraint) | Reference
		(pointer to an external resource). Pick one. `tags` is an ARRAY of free-form tag
		strings, normalised on write ([] clears, omit leaves as-is).
		`version` is the WATERMARK baseline: pass the store `currentVersion` from your last read OR
		the entry's own version — both valid; 0 = new; a version above the store cursor is rejected
		as a wrong-scope baseline. The guard is about PAYLOAD: an old baseline conflicts ONLY when
		the entry semantically moved after your read — a payload identical to the current state
		no-ops, and bookkeeping bumps auto-resolve (keys land in `autoResolved[]`). Set `prevKey`
		to rename.
		To delete an entry, pass { key, deleted:true } (optional version baseline) — it is
		soft-closed (history kept) and appears in the result's `removed`.
		Store durable facts not derivable from code/git/config; actionable work goes to a
		task board, not here.
		Returns the pure write-ack { applied, currentVersion, inserted, closed, conflicts[],
		added[], updated[], removed[], autoResolved[] }. `applied` is the SINGLE source of truth:
		FALSE = nothing
			written (conflicts[] carry each rejected key's baseline vs active version; a Stale
			conflict also names `changedFields` — THIS entry's fields that moved, rebase on those;
			added/updated/removed EMPTY; re-read via memory_delta to rebase). When TRUE,
			added/updated/removed cover ONLY this call's entries
		(never other writers' history — there is no cursor parameter on a write) and carry
		key/type/description/version; `body` follows the uniform bodyLen knob (omitted here = NO
		body, a compact ack; 0 = no body; N>0 = the first N chars, "…" when cut; -1 = full body).
			`scope`: project (default) | workspace. `currentVersion` is the store-wide cursor:
		for a full delta since a cursor, call memory_delta with it as `sinceVersion`.
		""")]
	public static async Task<MemoryUpsertResultView> UpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory,
		string projectKey, string store,
		[Description("Array of entry objects: { key, type, description, body, tags? (array of strings), metadata?, version?, prevKey? }, or { key, deleted:true } to soft-delete.")] MemoryEntryInputDto[] entries,
		[Description("Body length knob (uniform contract): omitted = NO body (the compact ack default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("project | workspace (default project).")] string? scope = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		projectKey = (await ResolveScopeAsync(http, db, projectKey, scope, ct)).Key;
		await AssertMemoryProjectAsync(http, db, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		var (upserts, deletes) = ParseEntries(entries);
		return Serialize(await memory.UpsertAsync(projectKey, store, upserts, deletes, ct), bodyLen);
	}

	[McpServerTool(Name = "memory_delta", Title = "Memory delta since cursor", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryUpsertResultView))]
	[Description("Return entries added/updated/removed since `sinceVersion` (no writes) — THE cursor/catch-up surface. `scope`: project (default) | workspace. Omit to CASCADE project first, then workspace — the same cascade contract as memory_search. Bodies follow the uniform bodyLen knob (compact by default). Requires memory:read.")]
	public static async Task<MemoryUpsertResultView> DeltaAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory,
		string projectKey, string store, long sinceVersion,
		[Description("Body length knob (uniform contract): omitted = NO body (compact default); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[Description("project | workspace; omit to cascade project first, then workspace.")] string? scope = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);

		foreach (var (scopeName, container) in await SearchContainersAsync(http, db, projectKey, scope, ct))
		{
			ct.ThrowIfCancellationRequested();
			try { await AssertMemoryProjectAsync(http, db, container, ct); }
			catch (UnauthorizedAccessException) { continue; }
			return Serialize(await memory.DeltaAsync(container, store, sinceVersion, ct), bodyLen);
		}

		throw new InvalidOperationException($"store '{store}' not found (scope: {(scope ?? "cascade project+workspace")})");
	}

	// ---- read + capture verbs: search / remember ----
	//
	// memory_search is THE read verb (spec uniform-entity-verbs v2: list = search without
	// a query; it replaced the former memory.list + memory.recall pair) and memory_remember
	// the low-ceremony capture verb. Both carry the `scope` dimension over the per-project
	// store files —
	//   project   → the key's own project  (default; the usual case)
	//   workspace → the caller's workspace memory container (WorkspaceMemory.ContainerKeyFor)
	//               — facts that span projects of ONE workspace. "$system" keeps the legacy
	//               "$workspace" key; every other workspace gets "$ws-{wsKey}".
	// `search` with no scope CASCADES both (project ⊕ caller's workspace) and returns rows
	// labelled by scope so precedence is visible (project first). Personal facts are carried
	// by type=User, not a separate container. Curated/temporal writes go through memory_upsert.

	// Legacy alias for the $system workspace container (seeded by M028/M031). Prefer
	// WorkspaceMemory.ContainerKeyFor for new code.
	internal const string WorkspaceContainer = WorkspaceMemory.SystemContainer;
	const string DefaultStore = "notes";

	// Authorize a KEY-ADDRESSED memory projectKey. Workspace containers ($workspace / $ws-*)
	// feed every project's memory cascade within their OWN workspace, so key-addressed
	// curation (memory_upsert/get/delta, memory_store_*, remember) may address them directly
	// — but only within the workspace that owns the container. A key qualifies when its
	// project claim is the wildcard "*" or names a project whose WorkspaceKey equals the
	// container's WorkspaceKey. For every other target this is exactly AssertProject, so no
	// non-memory module gains container access.
	static async Task AssertMemoryProjectAsync(IHttpContextAccessor http, PetBoxDb db, string projectKey, CancellationToken ct)
	{
		if (!WorkspaceMemory.IsWorkspaceContainer(projectKey))
		{
			ModuleMcp.AssertProject(http, projectKey);
			return;
		}
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (claim == ProjectScope.AllProjects) return;
		var rows = await db.Projects
			.Where(p => p.Key == projectKey || p.Key == claim)
			.Select(p => new { p.Key, p.WorkspaceKey })
			.ToListAsync(ct);
		var containerWs = rows.FirstOrDefault(p => p.Key == projectKey)?.WorkspaceKey;
		var callerWs = rows.FirstOrDefault(p => p.Key == claim)?.WorkspaceKey;
		if (containerWs is null || callerWs is null || !string.Equals(callerWs, containerWs, StringComparison.Ordinal))
			throw new UnauthorizedAccessException(
				$"ApiKey is not scoped to project '{projectKey}' (the shared container is reachable only by keys of projects in its workspace)");
	}

	[McpServerTool(Name = "memory_remember", Title = "Remember a fact", UseStructuredContent = true, OutputSchemaType = typeof(MemoryRememberResult))]
	[Description("""
		CREATE one durable fact verbatim (always a NEW entry; edits go via memory_upsert).
		`scope`: project (default) | workspace (cross-project shared within the caller's
		workspace). `type` taxonomy (User|Feedback|Project|Reference) — pick explicitly.
		Store durable facts not derivable from code/git/config; actionable work goes to a
		task board. Requires memory:write.
		[[full]]
		CREATE one durable fact, verbatim (always a new entry; edits go via memory_upsert).
		The low-ceremony way to store a learning.
		`text` (required) is the fact. `scope` picks the container: project (default —
		the key's project) | workspace (shared across projects of the caller's workspace).
		`store` groups entries within a scope (default "notes"). `type` is the taxonomy
		(User|Feedback|Project|Reference; default Project) — pick explicitly, no inference.
		`tags` is an array of free-form tag strings; `description` an optional one-line
		summary. A unique key is generated. Store durable facts not derivable from
		code/git/config; actionable work goes to a task board. Requires memory:write.
		Returns { id, scope, store, key }.
		""")]
	public static async Task<MemoryRememberResult> RememberAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory,
		string text, string? scope = null, string? projectKey = null, string? store = null,
		string? type = null, string[]? tags = null, string? description = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
		var container = await ResolveScopeAsync(http, db, projectKey, scope, ct);
		await AssertMemoryProjectAsync(http, db, container.Key, ct);
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

	[McpServerTool(Name = "memory_search", Title = "Read memory entries (list + search)", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemorySearchResultView))]
	[Description("""
		THE memory read verb — one tool for LISTING (no `q`) and hybrid SEARCH (`q`).
		Omit `scope` to CASCADE project ⊕ workspace (or pass project | workspace for one).
		Bodies follow the uniform `bodyLen` knob (omitted = a ~240-char snippet, -1 = full, or
		memory_get); each row's `version` is the CAS baseline for memory_upsert. Hard ~30k-char
		output budget. Requires memory:read.

		Cost — your context pays it. Same query, same rows: bodyLen:0 = 1x, default snippet
		~1.5-2x, bodyLen:-1 ~3x+ and unbounded per row — a single long entry can add thousands
		of chars on its own.
		Cheap path: search with bodyLen:0, read the descriptions, then memory_get the 1-3 keys
		you actually need. Use -1 only when you already know the keys and there are few.
		Pulling full bodies across a wide limit "just in case" is the most expensive habit
		available here: it routinely spends a third of the response budget on text you will not read.
		[[full]]
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
		candidate pool). Bodies follow the uniform `bodyLen` knob: omitted = a ~240-char snippet
		(the compact listing default), 0 = no body, N>0 = the first N chars ("…" when cut),
			-1 = the full body — or pull a full body with memory_get.

		`includeUsage` adds surfaced/opened/lastHitAt counters per row. Usage counts what was
		actually SENT (post-limit, post-budget): every DELIVERED row counts an impression, in
		both modes — but only a search (with `q`) counts as deliberate; a listing has no
		relevance behind it, so it bumps `surfaced` and never the deliberate value signal.
		The response has a HARD OUTPUT BUDGET (~30k serialized chars):
		overflowing rows are prefix-cut in result order and flagged `truncated:true` +
		`omitted` + a narrowing `hint`; no markers = the complete answer. Requires
		memory:read.

		ROW WEIGHT: a row's `description` is capped at ~160 chars ("…" when cut) in BOTH modes —
		the head of a row is priced per row, so it stays a one-liner; memory_get returns the full
		description (and the full body).

		Returns { items: [{ scope, store, key, type, description, body, tags, version }], retrievers? };
		`version` is the entry's CAS baseline for memory_upsert (pass it back to edit without a Stale round-trip).
		With `q` each row also carries `score` (the fused, freshness-blended relevance) and `retriever`
		("lexical" = lexically confirmed, "semantic" = surfaced by the vector leg alone); a semantic-only
		hit below the relevance floor is dropped, so `limit` is a CEILING, not a plan (a query can return fewer rows).
		""")]
	public static async Task<MemorySearchResultView> SearchAsync(
		IHttpContextAccessor http, FeatureFlags features, PetBoxDb db, IMemoryService memory, IMemoryUsageRecorder usage,
		[LogArg(LogArgMode.Presence)][Description("Search query. Omit for a deterministic listing (list = search without q).")] string? q = null,
		[LogArg][Description("project | workspace; omit to cascade both (rows labelled by scope, project first).")] string? scope = null,
		string? projectKey = null,
		[LogArg][Description("Narrow to one store within each scope (default: sweep every store except the sensitive ones).")] string? store = null,
		[Description("Taxonomy filter: User|Feedback|Project|Reference.")] string? type = null,
		[Description("Sort order: {by: relevance|created|updated, desc?}. Default: updated desc (listing) / relevance (with q).")] SortInput? sort = null,
		[LogArg][Description("Max rows returned (default 20; 0 = no cap — the output budget still applies).")] int? limit = null,
		[LogArg][Description("Body length knob (uniform contract): omitted = a ~240-char snippet (the compact listing default — fetch a full body with memory_get or bodyLen:-1); 0 = no body; N>0 = the first N chars (\"…\" when cut); -1 = the full body.")] int? bodyLen = null,
		[LogArg][Description("Include usage counters (surfaced/opened/lastHitAt) per row (default false).")] bool includeUsage = false,
		[Description("Usage-signal source of the impression this search records (with q): \"deliberate\" (default — a human/agent intentionally searched, counts toward the honest value signal) or \"machine\" (an automatic hook/context pull — bumps only the raw surfaced count, never the deliberate cut GC trusts). Automated wiring-kit pulls should pass \"machine\".")] string? usageSource = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.Memory);
		ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryRead);
		var hasQuery = !string.IsNullOrWhiteSpace(q);
		var cap = limit ?? DefaultLimit;
		// A bare search is deliberate intent; only an explicit usage:"machine" (automated hook
		// pulls) records the softer, un-counted-toward-value signal (spec: memoverhaul).
		var deliberate = !string.Equals(usageSource?.Trim(), "machine", StringComparison.OrdinalIgnoreCase);

		// Each collected row carries its scope RANK (cascade order — project first), the fused
		// relevance Score (so we can HONESTLY merge across scopes below) and the delivery FACTS
		// the telemetry needs about the row that never reach the wire (its container, its full
		// body length, its pre-decay fused score).
		var scored = new List<(double Score, int ScopeRank, MemorySearchHitView Row, DeliveryFacts Facts)>();
		// Aggregate provenance across every scope searched: lexical/semantic = OR of the
		// legs that ran; degraded = OR (any leg that wanted semantic but couldn't).
		SearchRetrievers? retrievers = null;
		var scopeRank = -1;
		foreach (var (scopeName, container) in await SearchContainersAsync(http, db, projectKey, scope, ct))
		{
			ct.ThrowIfCancellationRequested();
			// Skip containers the key cannot reach (foreign workspace container, etc.).
			try { await AssertMemoryProjectAsync(http, db, container, ct); }
			catch (UnauthorizedAccessException) { continue; }
			scopeRank++;
			// QUERY: give each scope the FULL cap so both pools compete on merit — the honest
			// cross-scope merge (by score) below decides the winners, not a greedy first-scope
			// grab. LISTING keeps the project-first cascade (project precedence): the remainder
			// after project fills, then stop when full.
			var remaining = hasQuery ? cap : (cap == 0 ? 0 : cap - scored.Count);
			if (!hasQuery && cap > 0 && remaining <= 0) break;
			var res = await memory.SearchEntriesAsync(container, new SearchRequest<MemoryEntryFilter, MemorySortBy>
			{
				Query = hasQuery ? q : null,
				Filter = new MemoryEntryFilter(store, type),
				Sort = ParseSort(sort),
				Limit = remaining,
				BodyLen = 0, // request FULL bodies; the adapter applies the uniform bodyLen contract below
			}, ct);
			if (res.Retrievers is { } r)
				retrievers = retrievers is { } agg
					// The reason survives the OR-merge across scopes: the first scope that degraded
					// owns it (a mute degraded:true is exactly what this leaf exists to kill).
					? new SearchRetrievers(agg.Lexical | r.Lexical, agg.Semantic | r.Semantic, agg.Degraded | r.Degraded,
						agg.DegradedReason ?? r.DegradedReason)
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
				var row = new MemorySearchHitView(scopeName, h.Store, h.Entry.Key, h.Entry.Type,
					// The row HEAD is priced per row too (spec: row-weight-bounded): an unbounded
					// description made the head, not the body, the bigger half of a search's cost.
					// Same uniform truncation contract as a body — the full text is one memory_get away.
					ModuleMcp.Body(h.Entry.Description, null, DescriptionSnippet) ?? "",
					// Uniform bodyLen contract, default a ~240-char snippet (compact listing).
					ModuleMcp.Body(h.Entry.Body, bodyLen, ModuleMcp.DefaultSnippet), h.Entry.Tags, h.Entry.Version,
					includeUsage ? (u?.Surfaced ?? 0) : null, includeUsage ? (u?.Opened ?? 0) : null, u?.LastHitAt,
					// W6 provenance surface: how many distinct sessions this fact was seen in
					// (compact — a number only). Null when it carries no session provenance.
					SourcesCount(h.Entry.Metadata),
					// Per-row relevance provenance (query mode only; null → omitted in a listing).
					Score: hasQuery ? Math.Round(h.Score, 6) : null, Retriever: h.Retriever);
				// The service was asked for FULL bodies (BodyLen: 0), so h.Entry.Body is the whole
				// entry — the denominator of "how much of it did this delivery actually send".
				// ScoreRaw is the PRE-decay fused score (null in a listing: no relevance leg ran).
				scored.Add((h.Score, scopeRank, row, new DeliveryFacts(
					container, scopeName, h.Store, h.Entry.Key, h.Entry.Body.Length,
					hasQuery ? h.ScoreRaw : null)));
			}
		}

		// Cross-scope honest merge (query mode, 2+ scopes contributed): the best hit wins
		// regardless of container — order by fused+decayed score, ties resolve project-first
		// (lower scope rank). With a single scope we keep the service's relevance order intact
		// (re-sorting by raw score would undo its MMR diversification); a listing keeps the
		// project-first cascade order it was collected in.
		var multiScope = scored.Select(s => s.ScopeRank).Distinct().Count() > 1;
		IEnumerable<(double Score, int ScopeRank, MemorySearchHitView Row, DeliveryFacts Facts)> orderedScored =
			hasQuery && multiScope
				// Quantize the score so genuine relevance/freshness gaps decide the order, but
				// sub-threshold noise (two rank-0 hits whose Updated differ by milliseconds) ties
				// and falls back to project-first — the documented cascade precedence. OrderBy is
				// stable, so within a scope the service's relevance/MMR order is preserved.
				? scored.OrderByDescending(s => Math.Round(s.Score, 6)).ThenBy(s => s.ScopeRank)
				: scored;
		var ordered = orderedScored.ToList();
		if (cap > 0 && ordered.Count > cap) ordered = ordered.Take(cap).ToList();
		var rows = ordered.Select(s => s.Row).ToList();

		// Response budget (MCP-adapter-only): measured on the wire form of the rows as they
		// will be sent (bodies already sliced by the service), prefix-cut, marked — never silent.
		var (kept, omitted) = new ResponseBudget().Take(rows);

		// What was ACTUALLY SENT: Take is a prefix cut, so the first kept.Count ordered rows are
		// the answer. Both usage record points below are driven by THIS list — a row dropped by
		// `cap` or by the response budget never reached the agent's context and must not count
		// (spec: usage-counts-what-was-sent).
		var delivered = ordered.Take(kept.Count).ToList();

		// Impression = the rows the answer DELIVERED. A LISTING delivers too (its rows land in the
		// caller's context exactly like a search's), so it bumps Surfaced — but it ran no relevance
		// leg, so it is never DELIBERATE: DeliberateCount is the value signal GC trusts, and "this
		// row happened to be listed" proves nothing about the fact's worth.
		foreach (var g in delivered.GroupBy(d => (d.Facts.Container, d.Facts.Store)))
			usage.Surfaced(g.Key.Container, g.Key.Store, g.Select(d => d.Facts.Key).ToList(),
				deliberate: hasQuery && deliberate);

		// Delivery telemetry (spec: usage-cost-and-fit-separate) — the cost/fit record of the same
		// delivered rows. The enqueue is fire-and-forget (the recorder drains in the background),
		// so the read path does not wait on it.
		RecordDeliveries(usage, delivered, scored,
			tool: hasQuery ? "search" : "listing",
			sessionId: McpSessionId(http),
			usageSource: deliberate ? DeliberateSource : MachineSource);

		return new MemorySearchResultView(
			kept,
			Retrievers: retrievers is { } fin ? new RetrieverInfo(fin.Lexical, fin.Semantic, fin.Degraded, fin.DegradedReason) : null,
			Truncated: omitted > 0 ? true : null,
			Omitted: omitted > 0 ? omitted : null,
			Hint: omitted > 0 ? SearchBudgetHint : null);
	}

	// The bounded default of memory_search (both modes; spec bounded-result-sets).
	const int DefaultLimit = 20;

	// The cap on a row's `description` in search/listing rows (spec: row-weight-bounded). A
	// description is by contract a ONE-LINE summary, and 160 chars is a full line of prose — long
	// enough to identify a fact, short enough that the row head cannot outweigh its body. It is
	// NOT caller-tunable (no per-field knob): the field is a head, not a payload; a longer text
	// means the entry's body was written into its description, and the fix for that is memory_get
	// (full description + full body), not a wider search row.
	const int DescriptionSnippet = 160;

	// The usage-signal split, as recorded on a delivery event (mirrors entry_usage's
	// deliberate/machine cut — see the `usageSource` argument of memory_search).
	const string DeliberateSource = "deliberate";
	const string MachineSource = "machine";

	// What a delivered row costs and how well it fitted — the parts that never reach the wire:
	// the CONTAINER it came from (the events land in that container's file), the entry's FULL
	// body length (the denominator of "how much of it did we send"), and the PRE-decay fused
	// score (null in a listing). The wire row carries the rest (scope/store/key/body).
	readonly record struct DeliveryFacts(
		string Container, string Scope, string Store, string Key, int BodyChars, double? ScoreRaw);

	// One delivery event per row actually SENT (spec: usage-cost-and-fit-separate). Cost and fit
	// stay separate and stay raw — no single "value" scalar is derived here.
	//
	// kRel normalizes fit WITHIN the request: ScoreRaw / the top-1 ScoreRaw of this same request.
	// Raw RRF has no absolute scale (its ceiling is ~1/60 ≈ 0.033 — see HybridMerge), so a bare
	// score is not comparable across requests; the denominator is taken over EVERY collected row
	// (pre-cap, all scopes — RRF scores are rank-based and therefore comparable across
	// containers), so it is the request's true best hit even when the cap dropped it. Rank is the
	// row's 1-based position in the delivered answer — MMR reorders rows without touching their
	// score, so rank and scoreRaw are two different facts and BOTH are stored.
	static void RecordDeliveries(
		IMemoryUsageRecorder usage,
		IReadOnlyList<(double Score, int ScopeRank, MemorySearchHitView Row, DeliveryFacts Facts)> delivered,
		IReadOnlyList<(double Score, int ScopeRank, MemorySearchHitView Row, DeliveryFacts Facts)> all,
		string tool, string? sessionId, string usageSource)
	{
		if (delivered.Count == 0) return;
		var top = all.Max(s => s.Facts.ScoreRaw ?? 0);
		var events = new List<MemoryDeliveryEvent>(delivered.Count);
		for (var i = 0; i < delivered.Count; i++)
		{
			var (_, _, row, f) = delivered[i];
			events.Add(new MemoryDeliveryEvent(
				Tool: tool, Scope: f.Scope, Store: f.Store, Key: f.Key,
				// The body as SENT (the bodyLen contract already applied) vs the whole entry.
				DeliveredChars: row.Body?.Length ?? 0, BodyChars: f.BodyChars,
				// The row's whole wire price — description, tags, envelope and all.
				RowChars: ResponseBudget.CostOf(row),
				Rank: i + 1,
				ScoreRaw: f.ScoreRaw,
				// A degenerate top-1 (no relevance leg, or a zero score) leaves fit unknown rather
				// than dividing by zero and claiming a perfect 1.
				KRel: f.ScoreRaw is { } s && top > 0 ? s / top : null,
				SessionId: sessionId,
				UsageSource: usageSource));
		}
		// Rows of one answer may span containers (the project ⊕ workspace cascade): each event
		// belongs in the file of the container it was read from.
		foreach (var g in events.Zip(delivered, (e, d) => (Event: e, d.Facts.Container))
			.GroupBy(x => x.Container, StringComparer.OrdinalIgnoreCase))
			usage.Delivered(g.Key, g.Select(x => x.Event).ToList());
	}

	// The MCP streamable-HTTP session id (the same one McpTracingFilter logs), read off the
	// request header — a tool method has no IMcpServer in scope, and it is null on a stateless
	// transport, which the event stores as-is.
	static string? McpSessionId(IHttpContextAccessor http) =>
		http.HttpContext?.Request.Headers["Mcp-Session-Id"].FirstOrDefault();

	// Surfaced on MemorySearchResultView.Hint when the rows were cut by the response budget.
	const string SearchBudgetHint =
		"Output budget exceeded: entries were truncated (see truncated/omitted). Narrow the " +
		"read: `scope`/`store` (one container/store), `type` (one taxonomy), a lower `limit`, " +
		"`bodyLen` (snippet bodies), or memory_get for one entry's full body.";

	// Map the wire `sort` argument onto the service sort axis; an unknown axis is a clear error.
	static (MemorySortBy By, bool Desc)? ParseSort(SortInput? sort)
	{
		if (sort is null || string.IsNullOrWhiteSpace(sort.By)) return null;
		if (!Enum.TryParse<MemorySortBy>(sort.By.Trim(), ignoreCase: true, out var by))
			throw new ArgumentException($"sort.by '{sort.By}' is not a sort axis (valid: relevance|created|updated)");
		return (by, sort.Desc);
	}

	// Resolve a single explicit scope to its container projectKey.
	// project → the key's project (ModuleMcp.ResolveProject: the explicit arg, else the claim,
	// else — for a "*" key — its default project); workspace → the caller's workspace memory
	// container (lazy-ensured, derived from that same resolved project). When projectKey is
	// already a workspace container and scope is omitted/project, treat as workspace (callers
	// may address $workspace / $ws-* directly as projectKey). A "*" key with NO default and no
	// projectKey resolves nothing and throws ArgumentException.
	static async Task<(string Scope, string Key)> ResolveScopeAsync(
		IHttpContextAccessor http, PetBoxDb db, string? projectKey, string? scope, CancellationToken ct)
	{
		var s = scope?.Trim().ToLowerInvariant();
		if ((s is null or "" or "project")
			&& projectKey is not null
			&& WorkspaceMemory.IsWorkspaceContainer(projectKey))
			return ("workspace", await DirectContainerAsync(db, projectKey, ct));

		return s switch
		{
			null or "" or "project" => ("project", ModuleMcp.ResolveProject(http, projectKey)),
			"workspace" => ("workspace", await ResolveCallerWorkspaceContainerAsync(http, db, projectKey, ct)),
			_ => throw new ArgumentException($"invalid scope '{s}' (project|workspace)"),
		};
	}

	// A container addressed DIRECTLY as projectKey. Its Projects row is lazy (created on first resolve),
	// so the first-ever direct write to a fresh workspace's shared memory has to materialize it — but
	// only when it names a workspace that EXISTS (WorkspaceMemory.EnsureAddressedContainerAsync); a
	// typo'd "$ws-nosuch" stays a rejection (McpProjectExistsFilter refuses it, and for a key the filter
	// skips, AssertMemoryProjectAsync below still does) rather than becoming a fresh container row.
	static async Task<string> DirectContainerAsync(PetBoxDb db, string container, CancellationToken ct)
	{
		await WorkspaceMemory.EnsureAddressedContainerAsync(db, container, ct);
		return container;
	}

	// Caller's project → WorkspaceKey → WorkspaceMemory.ContainerKeyFor, ensuring the row.
	// A direct container projectKey passes through (ensured the same way).
	static async Task<string> ResolveCallerWorkspaceContainerAsync(
		IHttpContextAccessor http, PetBoxDb db, string? projectKey, CancellationToken ct)
	{
		if (projectKey is not null && WorkspaceMemory.IsWorkspaceContainer(projectKey))
			return await DirectContainerAsync(db, projectKey, ct);
		// ResolveProject throws ArgumentException for a "*" key with no default and no
		// projectKey — intentional (nothing to derive a workspace from).
		var proj = ModuleMcp.ResolveProject(http, projectKey);
		return await WorkspaceMemory.ResolveAndEnsureContainerAsync(db, proj, ct);
	}

	// The ordered list of (scope, container) memory_search reads. A single scope → that
	// container; no scope → the full cascade, project first (most specific) ⊕ the caller's
	// workspace container. Never hardcodes bare "$workspace" unless that IS the caller's
	// container.
	//
	// The cascade is single-sourced on ModuleMcp.ResolveProject, so a "*" key WITH a default
	// project cascades over that default (⊕ its workspace) exactly like a project-scoped key —
	// the absent projectKey stays meaningful, it just now resolves.
	//
	// Both cascade legs remain best-effort on ArgumentException: a "*" key with NO default and
	// no projectKey has no project and no derivable workspace — skip that leg rather than failing
	// the whole search (admin/wiring bare memory_search("q") must degrade to empty, not throw).
	// Explicit scope=project|workspace still throws via ResolveScopeAsync when unresolvable.
	static async Task<List<(string Scope, string Key)>> SearchContainersAsync(
		IHttpContextAccessor http, PetBoxDb db, string? projectKey, string? scope, CancellationToken ct)
	{
		var s = scope?.Trim().ToLowerInvariant();
		if (!string.IsNullOrEmpty(s) && s != "all" && s != "cascade")
			return [await ResolveScopeAsync(http, db, projectKey, s, ct)];

		var list = new List<(string, string)>();
		string? resolvedProject = null;
		try { resolvedProject = ModuleMcp.ResolveProject(http, projectKey); list.Add(("project", resolvedProject)); }
		catch (ArgumentException) { /* "*" key, no default, no projectKey — skip project leg */ }
		catch (UnauthorizedAccessException) { /* projectKey doesn't match claim */ }

		// Workspace leg: need a project to derive the caller's workspace. Prefer the
		// resolved project; fall back to projectKey when it is already a container.
		var forWs = resolvedProject ?? projectKey;
		if (forWs is not null && WorkspaceMemory.IsWorkspaceContainer(forWs))
		{
			if (!list.Any(c => c.Item2 == forWs))
				list.Add(("workspace", forWs));
			return list;
		}
		try
		{
			var wsContainer = await ResolveCallerWorkspaceContainerAsync(http, db, forWs, ct);
			if (!list.Any(c => c.Item2 == wsContainer))
				list.Add(("workspace", wsContainer));
		}
		catch (ArgumentException)
		{
			// "*" with no default and no projectKey (or otherwise unresolvable) — skip the
			// workspace leg. Explicit scope=workspace still throws above via ResolveScopeAsync.
		}
		return list;
	}

	static string NormalizeStore(string? store) =>
		string.IsNullOrWhiteSpace(store) ? DefaultStore : store.Trim();

	// W6 provenance surface (spec memoverhaul-provenance-surface): the count of DISTINCT sessions
	// a fact was observed in, parsed cheaply from the entry metadata the session jobs stamp —
	// `sessionId` (string) ∪ `seenIn` (array) ∪ `sources` (array), the same union the autocapture
	// dedup measures as provenance width. Only a NUMBER goes on the wire (never the id list — the
	// output budget stays lean); null when the fact carries no session provenance (e.g. a hand
	// -written note), so the field is omitted rather than a noisy 0.
	static int? SourcesCount(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata)) return null;
		try
		{
			using var doc = System.Text.Json.JsonDocument.Parse(metadata);
			var root = doc.RootElement;
			if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
			var ids = new HashSet<string>(StringComparer.Ordinal);
			if (root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				var s = sid.GetString();
				if (!string.IsNullOrWhiteSpace(s)) ids.Add(s!);
			}
			foreach (var field in new[] { "seenIn", "sources" })
				if (root.TryGetProperty(field, out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
					foreach (var el in arr.EnumerateArray())
						if (el.ValueKind == System.Text.Json.JsonValueKind.String)
						{
							var s = el.GetString();
							if (!string.IsNullOrWhiteSpace(s)) ids.Add(s!);
						}
			return ids.Count > 0 ? ids.Count : null;
		}
		catch (System.Text.Json.JsonException)
		{
			return null; // opaque/unparseable metadata carries no countable provenance
		}
	}

	// ---- adapter plumbing: JSON parsing + wire shaping (no domain logic) ----

	static MemoryUpsertResultView Serialize(MemoryUpsertOutcome o, int? bodyLen = null)
	{
		var r = o.Result;
		return new MemoryUpsertResultView(
			Applied: r.Applied,
			CurrentVersion: r.CurrentVersion,
			Inserted: r.Inserted,
			Closed: r.Closed,
			Conflicts: r.Conflicts.Select(c => new MemoryConflictView(c.Key, c.Kind.ToString(), c.BaselineVersion, c.ActiveVersion, c.Reason, c.ChangedFields)).ToList(),
			Added: r.Added.Select(e => EntryDto(e, bodyLen)).ToList(),
			Updated: r.Updated.Select(e => EntryDto(e, bodyLen)).ToList(),
			Removed: r.Removed.ToList(),
			AutoResolved: r.AutoResolved.ToList());
	}

	// `body` is sliced to bodyLen (null when 0 → omitted by the serializer) so the write-echo
	// stays compact; `description` (a one-liner) is kept to orient the merge. Tags leave the
	// CSV storage form here — the surface speaks arrays.
	static MemoryEntryRow EntryDto(MemoryEntry e, int? bodyLen = null) => new(
		Key: e.Key,
		Type: e.Type.ToString(),
		Description: e.Description,
		Body: ModuleMcp.Body(e.Body, bodyLen, ModuleMcp.NoBody),
		Tags: string.IsNullOrWhiteSpace(e.Tags)
			? []
			: e.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
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
				Type = e.Version == 0 ? Req(e.Type, "type") : e.Type,
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
