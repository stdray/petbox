using PetBox.Tasks.Contract;

namespace PetBox.Web.Pages.Shared;

// Resolves an observation's raw ObservationSignalView.FixedByNodeId (a plain NodeId — it isn't a
// graph edge, so it never goes through the exhaustive relations panel's own LinkRef resolution in
// TasksService.GetNodeAsync) to a slug-addressable LinkDto, the SAME shape and SAME "resolvable ->
// slug route, else degrade to the opaque NodeId route" contract the relations panel already uses
// (TaskBoardNode.cshtml's node-links block) — reusing ITasksService.GetNodeAsync, the one door
// every other cross-board node lookup on this page already goes through, rather than a second
// resolution path. Never throws on a miss/deleted target: a null GetNodeAsync result becomes a
// LinkDto with Status "missing", which the rendering partial already knows how to fall back on.
public static class ObservationFixedByResolver
{
	public static async Task<LinkDto?> ResolveAsync(ITasksService tasks, string projectKey, string? fixedByNodeId, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(fixedByNodeId)) return null;
		var detail = await tasks.GetNodeAsync(projectKey, fixedByNodeId, ct);
		return detail is null
			? new LinkDto(fixedByNodeId, null, null, null, "missing")
			: new LinkDto(detail.Node.NodeId, detail.Board, detail.Node.Key, detail.Node.Title, detail.Node.Status);
	}

	// Batch variant for a card/table listing: resolves every DISTINCT FixedByNodeId across the
	// given observation signals in one pass. Bounded by how many observations on the page actually
	// carry a regression, never by board size — RecurredAfterFixAt is documented ("the single
	// highest-value signal") as a rare event, not a per-row cost that scales with the board.
	public static async Task<IReadOnlyDictionary<string, LinkDto>> ResolveManyAsync(
		ITasksService tasks, string projectKey, IEnumerable<ObservationSignalView?> observations, CancellationToken ct)
	{
		var ids = observations
			.Where(o => o?.FixedByNodeId is { Length: > 0 })
			.Select(o => o!.FixedByNodeId!)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		if (ids.Count == 0) return EmptyMap;

		var map = new Dictionary<string, LinkDto>(ids.Count, StringComparer.Ordinal);
		foreach (var id in ids)
			map[id] = await ResolveAsync(tasks, projectKey, id, ct) ?? new LinkDto(id, null, null, null, "missing");
		return map;
	}

	static readonly IReadOnlyDictionary<string, LinkDto> EmptyMap = new Dictionary<string, LinkDto>(StringComparer.Ordinal);
}
