using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services.Search;

// Projection altitude for unified-read rows. Listing mode needs the full TaskNodeView
// (parent/depth/links/delivery/commits) — same fields the board UI and MCP listing wire
// expose. Query mode only needs identity + body/tags/version/priority/timestamps for
// ranking, sort, and the MCP lean wire cut (spec search-lean-rows); relation panel work is
// wasted there.
public enum SearchProjectionKind
{
	Full,
	Lean,
}

// Builds TaskNodeView rows for search/list without going through GetAsync's full enrichment
// when Lean is enough. Pure projection — callers supply already-loaded tags (and, for Full,
// would use GetAsync instead; Full here is the identity-shaped shell only).
public static class TaskSearchProjector
{
	// Lean row: identity, body, tags, commits, decisionPending, originSessionId, version,
	// priority, timestamps, optional url.
	// Parent/depth/delivery/links/lineage left empty/null — query-mode MCP strips them on the
	// wire; sort axes that need Priority/Created/Updated still work.
	//
	// `commits` is the ONE former member of that stripped set that a lean row now carries
	// (client-issues/tasks-tool-contract-friction-tas-c31570). It is not enrichment here: the
	// `commit` reverse-lookup FILTER applies in query mode too, so leaving it empty meant a
	// query could SELECT rows by a commit and then hand back rows that showed none — the caller
	// had to re-read every hit with tasks_node_get just to see what it had matched on. Callers
	// that genuinely have no commit map (or want the cheapest possible row) pass null and get
	// the historic `[]`.
	private static TaskNodeView Lean(
		TaskNode n, string board, IReadOnlyList<string> tags, string? urlPrefix = null,
		IReadOnlyDictionary<string, List<string>>? commitsByNode = null) =>
		new(
			Key: n.Key,
			NodeId: n.NodeId,
			ParentNodeId: null,
			ParentSlug: null,
			Depth: 0,
			Status: n.Status,
			Type: n.Type,
			Title: n.Name,
			Body: n.Body,
			Commits: commitsByNode is not null && commitsByNode.TryGetValue(n.NodeId, out var cs) ? cs : [],
			Priority: n.Priority,
			Version: n.Version,
			Delivery: null,
			Spec: null,
			BlockedBy: null,
			LinkedTasks: null,
			Supersedes: null,
			RenamedFrom: [],
			Tags: tags,
			Url: urlPrefix is null ? null : urlPrefix + board + "/" + n.Key,
			CreatedAt: n.Created,
			UpdatedAt: n.Updated,
			// Both are COLUMNS of the row already in hand, so projecting them costs nothing and
			// keeps this view truthful: a lean domain row never claims a node has no origin just
			// because the projection was cheap. Whether they reach the WIRE in query mode is the
			// MCP adapter's lean cut (TasksTools.SearchRow), decided per field there.
			DecisionPending: n.DecisionPending,
			OriginSessionId: n.OriginSessionId,
			// NOT projected: the provenance UNION lives in plan_node_sessions and would cost an
			// extra board-wide read per query. null (not []) says exactly that — "not projected",
			// as distinct from "projected and empty" — so no consumer can mistake a lean row for
			// proof that a node has never been touched.
			OriginSessions: null);

	// Project every node in `nodes` to a lean view, keyed by slug and NodeId for hit resolve.
	public static (Dictionary<string, TaskNodeView> BySlug, Dictionary<string, TaskNodeView> ByNodeId)
		LeanIndex(string board, IEnumerable<TaskNode> nodes, ILookup<string, string> tagsByNode, string? urlPrefix = null,
			IReadOnlyDictionary<string, List<string>>? commitsByNode = null)
	{
		var bySlug = new Dictionary<string, TaskNodeView>(StringComparer.Ordinal);
		var byNodeId = new Dictionary<string, TaskNodeView>(StringComparer.Ordinal);
		foreach (var n in nodes)
		{
			var tags = tagsByNode[n.NodeId].OrderBy(t => t, StringComparer.Ordinal).ToList();
			var view = Lean(n, board, tags, urlPrefix, commitsByNode);
			bySlug[n.Key] = view;
			if (n.NodeId.Length > 0) byNodeId[n.NodeId] = view;
		}
		return (bySlug, byNodeId);
	}
}
