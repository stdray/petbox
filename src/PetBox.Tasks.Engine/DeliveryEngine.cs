namespace PetBox.Tasks.Workflow;

// The PURE half of the delivery roll-up (methodology-engine-extraction, slice 5). Cut along the
// same seam as GuardEngine: the SELECTION stays in TasksService.ComputeSpecDeliveryAsync (which
// still pays for the project-wide node scan and the one task_spec edge sweep), the JUDGEMENT
// moved here. Zero IO, zero linq2db — the service maps PlanNode -> NodeState (condition 4) and
// pre-groups the relation edges at the boundary, exactly as the guard slice does.
//
// Nothing about the judgement changed in the move: the bottom-up memoized walk, its cycle guard,
// the not_started default for a task-less leaf and the combining order are the code that used to
// live inline in the service, relocated verbatim.
public static class DeliveryEngine
{
	// COMPUTED delivery roll-up (keyed by NodeId): a node's delivery derives bottom-up from the
	// tasks linked (task_spec) to it and its part_of descendants (decomposition may cross boards)
	// — each leaf resolves independently before combining, rather than pooling every task in the
	// subtree into one flat union. Type roles come from `def` (methodology data), not string
	// literals (primitives-enum-residual).
	//
	// `nodes` is EVERY active node of the project, not just the board being rolled up: a task
	// linked to a spec node normally lives on another board, and part_of decomposition may cross
	// boards too. `parentOf` maps child nodeId -> parent nodeId; `tasksOf` maps a spec nodeId ->
	// the nodeIds of the tasks linked INTO it. A task whose nodeId is absent from `nodes` (closed
	// out, or on a board the caller could not see) is skipped, not counted as unfinished.
	public static Dictionary<string, string> Rollup(
		MethodologyRuntime runtime,
		MethodologyDeliveryDef def,
		IReadOnlyList<string> specNodeIds,
		IReadOnlyList<NodeState> nodes,
		IReadOnlyDictionary<string, string> parentOf,
		IReadOnlyDictionary<string, IReadOnlyList<string>> tasksOf)
	{
		// Last write wins on a duplicate nodeId, which is what the service's board-by-board scan
		// did when it filled this map. NodeState carries Key/PrevKey too; this solver reads only
		// NodeId/Status/Type off it — a strict subset, so the projection needs no new field.
		var byNodeId = new Dictionary<string, (string Type, string Status)>(StringComparer.Ordinal);
		foreach (var n in nodes) byNodeId[n.NodeId] = (n.Type, n.Status);

		// childrenOf: invert part_of.
		var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var (child, parent) in parentOf)
			(childrenOf.TryGetValue(parent, out var l) ? l : childrenOf[parent] = []).Add(child);

		// Bottom-up, memoized: a node's delivery combines its OWN directly-linked tasks (if any)
		// with each child's recursively-computed delivery, via the same Combine used for tag-group
		// rollups. A leaf with no linked tasks has no inputs at all, so Combine defaults it to
		// not_started — it no longer disappears into a flat subtree-wide task union (which used to
		// hide task-less leaves and let an umbrella wrongly read as `done` when some leaves had no
		// work whatsoever).
		var memo = new Dictionary<string, string>(StringComparer.Ordinal);
		var visiting = new HashSet<string>(StringComparer.Ordinal);
		string DeliveryOf(string nodeId)
		{
			if (memo.TryGetValue(nodeId, out var cached)) return cached;
			if (!visiting.Add(nodeId)) return "not_started"; // cycle guard; part_of shouldn't cycle
			string? own = tasksOf.TryGetValue(nodeId, out var ts)
				? OwnDelivery(ts.Where(byNodeId.ContainsKey).Select(id => byNodeId[id]).ToList(), runtime, def)
				: null;
			var childDeliveries = childrenOf.TryGetValue(nodeId, out var kids)
				? kids.Select(DeliveryOf)
				: Enumerable.Empty<string>();
			var result = Combine(own is null ? childDeliveries : childDeliveries.Append(own)) ?? "not_started";
			visiting.Remove(nodeId);
			memo[nodeId] = result;
			return result;
		}

		return specNodeIds.ToDictionary(id => id, DeliveryOf, StringComparer.Ordinal);
	}

	// Roll up a group's per-node delivery into one status. not_started if all are (or none);
	// done only if all done; done_with_defects if all terminal with a defect; else in_progress.
	// Shared with the tag-group projection in the service, which rolls up an arbitrary bucket of
	// nodes rather than a part_of subtree.
	public static string? Combine(IEnumerable<string?> deliveries)
	{
		var ds = deliveries.Where(d => d is not null).Select(d => d!).ToList();
		if (ds.Count == 0 || ds.All(d => d == "not_started")) return "not_started";
		if (ds.All(d => d == "done")) return "done";
		if (ds.All(d => d is "done" or "done_with_defects")) return "done_with_defects";
		return "in_progress";
	}

	// One node's OWN delivery, from the tasks linked directly to it. Type roles from methodology
	// data: RequiredTypes drive progress; DefectTypes open while requireds are done yield
	// done_with_defects. No hardcoded "feature"/"bug" here.
	static string OwnDelivery(List<(string Type, string Status)> tasks, MethodologyRuntime runtime, MethodologyDeliveryDef def)
	{
		var required = tasks.Where(t => def.RequiredTypes.Contains(t.Type, StringComparer.OrdinalIgnoreCase)).ToList();
		if (required.Count == 0) return "not_started";
		if (!required.All(f => runtime.KindOfSlug(f.Status) == StatusKind.TerminalOk)) return "in_progress";
		var openDefect = tasks.Any(t =>
			def.DefectTypes.Contains(t.Type, StringComparer.OrdinalIgnoreCase)
			&& runtime.KindOfSlug(t.Status) == StatusKind.Open);
		return openDefect ? "done_with_defects" : "done";
	}
}
