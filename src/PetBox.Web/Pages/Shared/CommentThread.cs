using PetBox.Tasks.Contract;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.Shared;

// One rendered line of a comment thread: the comment plus its nesting depth. The flat
// CommentView list (each carrying parentId) is turned into this DFS-ordered shape so the
// view just iterates with an indent — the same shape the plan-node list uses.
public sealed record CommentLine(CommentView Comment, int Depth);

// Model for the _CommentThread partial: the DFS-flattened lines plus the project's optional
// commit-view URL template and `[[slug]]` node-mention map, threaded down so comment bodies
// autolink commit hashes and node mentions the same way node bodies do. Null template / null
// map = plain text (the pre-feature behavior). NodeId is the owning node's stable PlanNode.NodeId
// (comments-ui-edit): the add/reply forms carry it as a hidden field so a POST on the board page
// (many node cards, one page) still names the right node — the node detail page ignores the
// field and resolves the node from its own bound route instead, but the same partial/markup
// serves both surfaces.
public sealed record CommentThreadModel(
	IReadOnlyList<CommentLine> Lines,
	string NodeId,
	string? CommitUrlTemplate = null,
	IReadOnlyDictionary<string, NodeRefTarget>? NodeRefs = null);

// Shared thread flattener used by both the board page and the node detail page (so the two
// surfaces render the SAME thread shape via the _CommentThread partial). Pure/static.
public static class CommentThread
{
	// Flatten one node's flat comment list into DFS order with a depth, building the tree
	// from ParentId (siblings chronological). An unknown/missing parent is treated as a root
	// (defensive); a visited-set guards against any parentId cycle.
	public static IReadOnlyList<CommentLine> Flatten(IEnumerable<CommentView> comments)
	{
		var list = comments.ToList();
		var ids = list.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
		var byParent = list.Where(c => c.ParentId is not null && ids.Contains(c.ParentId))
			.ToLookup(c => c.ParentId!, StringComparer.Ordinal);
		var roots = list
			.Where(c => c.ParentId is null || !ids.Contains(c.ParentId))
			.OrderBy(c => c.Created);

		var ordered = new List<CommentLine>(list.Count);
		var visited = new HashSet<string>(StringComparer.Ordinal);
		void Emit(CommentView c, int depth)
		{
			if (!visited.Add(c.Id)) return;
			ordered.Add(new CommentLine(c, depth));
			foreach (var kid in byParent[c.Id].OrderBy(k => k.Created))
				Emit(kid, depth + 1);
		}
		foreach (var r in roots) Emit(r, 0);
		return ordered;
	}
}
