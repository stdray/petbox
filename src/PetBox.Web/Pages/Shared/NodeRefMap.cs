using PetBox.Tasks.Contract;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages.Shared;

// Bridges the tasks service to the markdown renderer for `[[slug]]` node mentions
// (node-ref-autolink): scan the about-to-render bodies for mention candidates, batch-resolve
// them through ITasksService (current + former slugs, ambiguous/miss dropped), and build the
// slug→target map the renderer consumes. Kept in one place so the board and node-detail pages
// wire it identically. Returns an empty map when nothing resolves.
public static class NodeRefMap
{
	public static async Task<IReadOnlyDictionary<string, NodeRefTarget>> BuildAsync(
		ITasksService tasks, string workspaceKey, string projectKey,
		IEnumerable<string?> bodies, CancellationToken ct)
	{
		var slugs = NodeRefs.ExtractSlugs(bodies);
		if (slugs.Count == 0)
			return EmptyMap;

		var resolved = await tasks.ResolveSlugsAsync(projectKey, slugs, ct);
		if (resolved.Count == 0)
			return EmptyMap;

		var map = new Dictionary<string, NodeRefTarget>(resolved.Count, StringComparer.Ordinal);
		foreach (var (slug, r) in resolved)
		{
			// href targets the node's CURRENT location (Board+Key); title = its title (fall back to
			// the key for a useful tooltip). Link text stays the mentioned slug (the renderer's job).
			var url = Routes.TaskBoardNodeBySlug(workspaceKey, projectKey, r.Board, r.Key);
			map[slug] = new NodeRefTarget(url, string.IsNullOrEmpty(r.Title) ? r.Key : r.Title);
		}
		return map;
	}

	static readonly IReadOnlyDictionary<string, NodeRefTarget> EmptyMap
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);
}
