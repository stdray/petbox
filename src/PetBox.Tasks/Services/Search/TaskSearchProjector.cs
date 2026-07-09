using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;

namespace PetBox.Tasks.Services.Search;

// Projection altitude for unified-read rows. Listing mode needs the full PlanNodeView
// (parent/depth/links/delivery/commits) — same fields the board UI and MCP listing wire
// expose. Query mode only needs identity + body/tags/version/priority/timestamps for
// ranking, sort, and the MCP lean wire cut (spec search-lean-rows); relation panel work is
// wasted there.
public enum SearchProjectionKind
{
	Full,
	Lean,
}

// Builds PlanNodeView rows for search/list without going through GetAsync's full enrichment
// when Lean is enough. Pure projection — callers supply already-loaded tags (and, for Full,
// would use GetAsync instead; Full here is the identity-shaped shell only).
public static class TaskSearchProjector
{
	// Lean row: identity, body, tags, version, priority, timestamps, optional url.
	// Parent/depth/delivery/links/commits/lineage left empty/null — query-mode MCP strips
	// them on the wire; sort axes that need Priority/Created/Updated still work.
	public static PlanNodeView Lean(
		PlanNode n, string board, IReadOnlyList<string> tags, string? urlPrefix = null) =>
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
			Commits: [],
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
			UpdatedAt: n.Updated);

	// Project every node in `nodes` to a lean view, keyed by slug and NodeId for hit resolve.
	public static (Dictionary<string, PlanNodeView> BySlug, Dictionary<string, PlanNodeView> ByNodeId)
		LeanIndex(string board, IEnumerable<PlanNode> nodes, ILookup<string, string> tagsByNode, string? urlPrefix = null)
	{
		var bySlug = new Dictionary<string, PlanNodeView>(StringComparer.Ordinal);
		var byNodeId = new Dictionary<string, PlanNodeView>(StringComparer.Ordinal);
		foreach (var n in nodes)
		{
			var tags = tagsByNode[n.NodeId].OrderBy(t => t, StringComparer.Ordinal).ToList();
			var view = Lean(n, board, tags, urlPrefix);
			bySlug[n.Key] = view;
			if (n.NodeId.Length > 0) byNodeId[n.NodeId] = view;
		}
		return (bySlug, byNodeId);
	}
}
