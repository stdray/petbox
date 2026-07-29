using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.Web.Search;

namespace PetBox.Web.Mcp;

// MCP surface for search MAINTENANCE (the read verbs live on the entities themselves —
// memory_search / tasks_search / session_search).
//
// Why an MCP tool and not an admin HTTP endpoint or a self-healing job:
//   * the operator here IS an agent — a reindex is diagnosed from memory_search's provenance
//     (`semantic:true, degraded:false` and still zero hits) and verified with the very same tool,
//     in one conversation, over the transport the maintainer already has authenticated;
//   * every other cross-cutting maintenance verb in PetBox (project_*, apikey_*, db_*, llm_*) is
//     already an MCP tool with scope+project guards — an HTTP admin route would be a second,
//     unmirrored auth surface for one button;
//   * it must NOT be automatic. A reindex re-embeds a project's entire corpus; a job that decided
//     on its own to rewind cursors whenever it smelled an empty index would be a foot-gun with a
//     hair trigger (and would fight the very dead-letter protection it is built to undo).
// It is a THIN adapter: guards + tier parsing, all state work in SearchReindexService.
// TENANT DECLARATION (spec authz-scope-declaration): the `projectKey` ARGUMENT — OPTIONAL here, so
// the PEP falls back to CallerTenant.DefaultProjectOf, which is the same fallback
// ModuleMcp.ResolveProject takes two lines down. One answer to "which project is this key's own?",
// asked twice, by construction rather than by two implementations that happen to agree.
//
// ResolveProject STAYS: its RETURN VALUE is the project the reindex runs against, so it is the
// resolver, not a duplicate check. Its authorization side is now redundant on this tool AND on the
// memory family — that family declares [TenantFrom(ArgumentOrContainer, "projectKey")] and the
// allowlist is empty, so both are under the PEP. The guard stays anyway, because for the memory verbs
// it is no longer the same question: the PEP authorizes the ONE tenant the call NAMED, while those
// verbs go on to act on a container they DERIVE (see the header of MemoryTools.cs). A single-target
// gate cannot express that, so this is a justified remainder rather than a second answer.
//
// NOTE the declaration here is plain Argument, NOT ArgumentOrContainer: a workspace memory container
// (`$workspace` / `$ws-<key>`) is not a valid projectKey for this tool, and aiming one at it is a
// claim mismatch, refused by the PEP. That is deliberate scoping, not an oversight — the container
// decode is granted only to the surface that declared it.
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class SearchTools
{
	[McpServerTool(Name = "search_reindex", Title = "Rebuild a project's search index",
		Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(SearchReindexResult))]
	[Description("""
		MAINTENANCE: rebuild a project's search index from scratch — BOTH the semantic (vector,
		Class-B) and lexical (full-text, Class-A) halves. Use it when search reports
		`semantic:true, degraded:false` and STILL returns nothing (documents dead-lettered during
		an embedder outage, cursor advanced past them), or when the lexical floor looks stale/wrong
		(e.g. after a projection change) and you don't want to wait for an incidental search to
		trigger the self-heal.

		CLASS-B (vector): resets, per named index (memory: `vector:{store}`; tasks: the bare board
		name — no other index in those files is touched), the dead-letter (condemned docs eligible
		again) and the version cursor (back to 0, so the whole store is a delta again). Re-embedding
		is then done by the STOCK background enrichment drain (60s tick), in capped portions — this
		tool returns immediately; `activeDocs` per tier is how many `search_vec` rows to expect once
		it finishes (a few minutes). VERIFY by searching again, or from the drain's own log lines
		(events 410/411: `search_vec rows N, dead total 0, max cursor lag 0`).

		CLASS-A (lexical): rewinds the lexical projection marker(s) (memory: one per store; tasks:
		one for the whole file) to 0 — `lexicalReset` per tier is how many were rewound. No drain to
		wait for: the next search runs the lexical backfill synchronously and rebuilds search_fts
		from the current ToDoc projection, before that search's own results come back.

		Do NOT re-run this tool to check progress on the vector half: it is idempotent but it would
		rewind the cursor and re-embed the corpus from the start.

		REFUSES (and resets NEITHER half) when the project has no working Embed route — fix the LLM
		route first. `tier`: memory | tasks | all (default all). Requires memory:write / tasks:write.

		`projectKey` takes a REAL PROJECT KEY ONLY. A workspace shared-memory container
		(`$workspace` / `$ws-<key>`) is not accepted here — only the memory_* verbs decode a container
		key — so passing one is refused as a claim mismatch ("ApiKey is not scoped to project …"),
		which reads like a permissions problem but is an addressing one. There is currently no
		reindex path for a workspace container's memory through this tool.
		""")]
	public static async Task<SearchReindexResult> ReindexAsync(
		IHttpContextAccessor http, FeatureFlags features, SearchReindexService reindex,
		string? projectKey = null,
		[Description("memory | tasks | all (default all — every tier whose feature is enabled).")] string? tier = null,
		CancellationToken ct = default)
	{
		var project = await ModuleMcp.ResolveProject(http, projectKey, ct);
		var requested = Parse(tier);

		// `all` covers the tiers this deployment actually has switched on; an EXPLICIT tier whose
		// feature is off is a caller error, and says so.
		var memory = requested is ReindexTier.Memory or ReindexTier.All && features.IsEnabled(Feature.Memory);
		var tasks = requested is ReindexTier.Tasks or ReindexTier.All && features.IsEnabled(Feature.Tasks);
		if (requested == ReindexTier.Memory && !memory) ModuleMcp.AssertFeature(features, Feature.Memory);
		if (requested == ReindexTier.Tasks && !tasks) ModuleMcp.AssertFeature(features, Feature.Tasks);
		if (!memory && !tasks)
			throw new InvalidOperationException("no searchable tier is enabled (memory / tasks)");

		// A reindex rewrites the tier's Class-B state → the tier's WRITE scope, one per tier touched.
		if (memory) ModuleMcp.AssertScope(http, ApiKeyScopes.MemoryWrite);
		if (tasks) ModuleMcp.AssertScope(http, ApiKeyScopes.TasksWrite);

		var effective = memory && tasks ? ReindexTier.All : memory ? ReindexTier.Memory : ReindexTier.Tasks;
		return await reindex.ReindexAsync(project, effective, ct);
	}

	static ReindexTier Parse(string? tier) => tier?.Trim().ToLowerInvariant() switch
	{
		null or "" or "all" => ReindexTier.All,
		"memory" => ReindexTier.Memory,
		"tasks" => ReindexTier.Tasks,
		var other => throw new ArgumentException($"unknown tier '{other}' (memory | tasks | all)"),
	};
}
