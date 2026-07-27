using System.Security.Claims;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Search;
using PetBox.Memory.Contract;

namespace PetBox.Web.Memory;

// UI-side twin of MemoryTools.SearchContainersAsync (spec search-one-engine-for-human-and-agent:
// the human surface and the agent surface run the SAME hybrid engine — SearchEntriesAsync — with
// the same scope cascade, just resolved differently). The MCP adapter derives the caller's
// workspace container from an API-key claim; a Razor page already KNOWS {workspaceKey,
// projectKey} from its route, so the resolution here is a plain lookup, no claims involved for the
// PROJECT leg. The WORKSPACE leg is still a DERIVED container the route's PEP never judged
// (WorkspaceViewer only authorized the route's own project) — same hop MemoryRefMap gates, same
// shape here: SandboxContainment.PermitsAsync is asked before that leg is ever read, and a
// sandboxOnly principal without entitlement silently loses the leg rather than the whole request
// (see ResolveContainersAsync).
public static class MemorySearchScope
{
	// One selected hit, labelled by which container it came from ("project" | "workspace") — the
	// UI equivalent of MCP's MemorySearchHitView.Scope. Created/Updated ride along from
	// MemoryEntryHit (not on Entry itself — MemoryEntryView is the wire-facing projection and
	// deliberately doesn't carry them) so a LISTING caller can build a KeysetCursor's sort-key
	// value without a second round-trip.
	public sealed record Row(string Scope, string Store, MemoryEntryView Entry, double Score, string? Retriever,
		DateTime Created, DateTime Updated);

	public sealed record Result(IReadOnlyList<Row> Rows, SearchRetrievers? Retrievers);

	// Whether offering the scope control makes sense at all: moot when the project IS already the
	// workspace's shared-memory container (ResolveContainersAsync collapses to one leg regardless
	// of what's chosen). Pure UI-display logic — reads no storage, needs no containment check.
	public static bool IsScopeSelectable(string workspaceKey, string projectKey) =>
		!string.Equals(WorkspaceMemory.ContainerKeyFor(workspaceKey), projectKey, StringComparison.Ordinal);

	// project   → the project's own container only (the default — matches today's page).
	// workspace → the workspace's shared-memory container only.
	// cascade   → both, honestly merged by fused score below (project wins ties — same
	//             precedence as MemoryTools.SearchAsync's cross-scope merge).
	// Collapses to a single "project" leg when the two containers already coincide (viewing a
	// $ws-*/$workspace container directly) — same special case MemoryRefMap/MemoryModel apply.
	//
	// `catalog` is null-forgiven at the call to PermitsAsync rather than typed non-nullable, the
	// same posture TaskBoardModel/TaskBoardNodeModel give MemoryRefMap.BuildAsync: PermitsAsync
	// never dereferences it unless SandboxContainment.AppliesTo(user) is true, and every caller of
	// this method today is a Cookie-scheme WorkspaceViewer page — the one claim kind that carries
	// SandboxOnly is an api-key identity, which that policy challenges to /Login before reaching
	// here (same reasoning MemoryRefMap's class header spells out).
	public static async Task<IReadOnlyList<(string Scope, string Container)>> ResolveContainersAsync(
		ClaimsPrincipal? user, IProjectCatalog? catalog, string workspaceKey, string projectKey, string? scope, CancellationToken ct)
	{
		var wsContainer = WorkspaceMemory.ContainerKeyFor(workspaceKey);
		if (string.Equals(wsContainer, projectKey, StringComparison.Ordinal))
			return [("project", projectKey)];

		var s = scope?.Trim().ToLowerInvariant();
		if (s is not ("workspace" or "cascade"))
			return [("project", projectKey)];

		// THE DERIVED HOP: the route's PEP (WorkspaceViewer + TenantFrom Route projectKey) judged
		// only the route's own project — it never judged this workspace container. Ask again here,
		// exactly as MemoryRefMap/MemoryApi.CanonAsync do, before the leg is ever read.
		var permitted = await SandboxContainment.PermitsAsync(user, wsContainer, catalog!, ct);
		if (!permitted) return s == "cascade" ? [("project", projectKey)] : [];
		return s == "cascade" ? [("project", projectKey), ("workspace", wsContainer)] : [("workspace", wsContainer)];
	}

	// Runs `request` against every container the scope selection names and merges the rows. With
	// a query (relevance selection) and 2+ containers, the merge is HONEST — ordered by the fused
	// score, project-first on ties — mirroring MemoryTools.SearchAsync's cross-scope merge so the
	// UI and the agent surface never disagree on which hit "wins". A single-container scope keeps
	// the service's own order (relevance/MMR or the deterministic listing order) untouched.
	public static async Task<Result> SearchAsync(
		IMemoryService memory, ClaimsPrincipal? user, IProjectCatalog? catalog,
		string workspaceKey, string projectKey, string? scope,
		SearchRequest<MemoryEntryFilter, MemorySortBy> request, CancellationToken ct)
	{
		var containers = await ResolveContainersAsync(user, catalog, workspaceKey, projectKey, scope, ct);
		var hasQuery = !string.IsNullOrWhiteSpace(request.Query);
		var collected = new List<(double Score, int Rank, Row Row)>();
		SearchRetrievers? retrievers = null;
		for (var rank = 0; rank < containers.Count; rank++)
		{
			var (scopeName, container) = containers[rank];
			var res = await memory.SearchEntriesAsync(container, request, ct);
			if (res.Retrievers is { } r)
				retrievers = retrievers is { } agg
					// Ranking merges through MemoryTools.MergeRanking — the SAME merge the MCP surface
					// uses, deliberately reused rather than re-derived. The tri-state's whole point is
					// that a degradation in one leg must not hide behind another leg's success, and a
					// cascade whose workspace container has no rerank route is a PERMANENT arrangement,
					// not an outage: resolving toward the flattering value would report `Reranked`
					// forever while half the rows were plain RRF.
					? new SearchRetrievers(agg.Lexical | r.Lexical, agg.Semantic | r.Semantic, agg.Degraded | r.Degraded,
						agg.DegradedReason ?? r.DegradedReason,
						agg.SemanticLag is null && r.SemanticLag is null ? null : (agg.SemanticLag ?? 0) + (r.SemanticLag ?? 0),
						PetBox.Web.Mcp.MemoryTools.MergeRanking(agg.Ranking, r.Ranking))
					: r;
			foreach (var h in res.Hits)
				collected.Add((h.Score, rank, new Row(scopeName, h.Store, h.Entry, h.Score, h.Retriever, h.Created, h.Updated)));
		}

		var multiScope = containers.Count > 1;
		IEnumerable<(double Score, int Rank, Row Row)> ordered = hasQuery && multiScope
			// Quantized so genuine score gaps decide order but sub-threshold noise falls back to
			// the documented project-first cascade precedence (same rounding MemoryTools uses).
			? collected.OrderByDescending(x => Math.Round(x.Score, 6)).ThenBy(x => x.Rank)
			: collected;
		var rows = ordered.Select(x => x.Row).ToList();
		if (request.Limit > 0 && rows.Count > request.Limit) rows = rows.Take(request.Limit).ToList();
		return new Result(rows, retrievers);
	}

	// Batched usage-counter lookup for a page of Rows, grouped by (Scope, Store) so each
	// container/store pair costs one GetUsageAsync call regardless of how many rows it
	// contributed. Keyed by "scope\x1fstore\x1fkey" (mirrors MemoryTools' "store\x1fkey" usage
	// map key, with the scope leg added since rows here may span containers). Re-derives the SAME
	// containment-gated container list as SearchAsync — a row only ever names a scope this method
	// itself resolved, so the lookup can never be tricked into reading a leg the search didn't.
	public static async Task<IReadOnlyDictionary<string, MemoryUsageView>> LoadUsageAsync(
		IMemoryService memory, ClaimsPrincipal? user, IProjectCatalog? catalog,
		string workspaceKey, string projectKey, string? scope, IReadOnlyList<Row> rows, CancellationToken ct)
	{
		if (rows.Count == 0) return EmptyUsage;
		var containers = (await ResolveContainersAsync(user, catalog, workspaceKey, projectKey, scope, ct))
			.ToDictionary(c => c.Scope, c => c.Container, StringComparer.Ordinal);
		var usage = new Dictionary<string, MemoryUsageView>(StringComparer.Ordinal);
		foreach (var g in rows.GroupBy(r => (r.Scope, r.Store)))
		{
			if (!containers.TryGetValue(g.Key.Scope, out var container)) continue;
			var keys = g.Select(r => r.Entry.Key).ToList();
			foreach (var kv in await memory.GetUsageAsync(container, g.Key.Store, keys, ct))
				usage[UsageKey(g.Key.Scope, g.Key.Store, kv.Key)] = kv.Value;
		}
		return usage;
	}

	public static string UsageKey(string scope, string store, string key) => scope + "\x1f" + store + "\x1f" + key;

	static readonly IReadOnlyDictionary<string, MemoryUsageView> EmptyUsage =
		new Dictionary<string, MemoryUsageView>(StringComparer.Ordinal);
}
