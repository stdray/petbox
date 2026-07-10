using LinqToDB;
using LinqToDB.Async;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services.Search;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Upsert;

// Post-write association stage for Tasks upsert: tags, commits, part_of, supersedes.
// New association kinds land here so UpsertAsync stays an orchestrator of stages.
public sealed class TaskUpsertAssociations
{
	readonly ITaskBoardStore _boards;
	readonly IRelationStore _relations;
	readonly ITagStore _tags;
	readonly TaskTransitionEffects _effects;

	public TaskUpsertAssociations(
		ITaskBoardStore boards, IRelationStore relations, ITagStore tags, TaskTransitionEffects effects)
	{
		_boards = boards;
		_relations = relations;
		_tags = tags;
		_effects = effects;
	}

	// Apply enforced tags after the upsert. A patch whose Tags is null OMITS tags (leave
	// as-is); a non-null list (incl. empty) is the new full set for that node. Tags bind
	// to the node's stable NodeId.
	public async Task SetTagsAsync(
		string projectKey, string board, MethodologyRuntime runtime, string? kindSlug,
		IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		// ONE RULE for every kind (primitives-tag-axes + methodology-instance-scoped-axes):
		// the kind's TAG AXES on this board's runtime drive enforcement — none = free-form
		// tags (any namespace + bare words), declared = enforced with the axes as the
		// namespace allowlist (bare tags rejected). The runtime is instance-scoped when the
		// board has MethodologyInstance membership (else the project-singleton def).
		// Quartet presets carry the builtin area/concern axes, `simple` carries none, and a
		// definition-resolved kind follows the definition's axes — so "methodology boards
		// enforce, simple doesn't" flows from axes-emptiness, not a hardcoded pair.
		var axes = runtime.TagAxes(kindSlug);
		var enforceNs = axes.Count > 0;
		var namespaces = enforceNs ? axes.Select(a => a.Namespace).ToList() : null;
		var nodeIdOf = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var p in patches)
		{
			if (p.Tags is null) continue;
			if (nodeIdOf.TryGetValue(p.Key, out var nid) && nid.Length > 0)
				await _tags.SetAsync(projectKey, board, nid, p.Tags, enforceNs, namespaces, ct);
		}
	}

	// Apply attached commits after the upsert (node-commits-impl), mirroring SetTagsAsync +
	// the SCD-2 tag write. A patch whose Commits is null OMITS them (leave as-is); a non-null
	// list (incl. empty) is the node's new full commit set. Commits bind to the stable NodeId.
	public static async Task SetCommitsAsync(
		TasksDb ctx, string board, IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		if (!patches.Any(p => p.Commits is not null)) return;
		var nodeIdOf = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var p in patches)
		{
			if (p.Commits is null) continue;
			if (!nodeIdOf.TryGetValue(p.Key, out var nid) || nid.Length == 0) continue;

			var desiredSet = NormalizeCommits(p.Commits);
			var active = await ctx.PlanNodeCommits.Where(c => c.NodeId == nid && c.ValidTo == null).ToListAsync(ct);
			var activeShas = active.Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
			var now = DateTime.UtcNow;

			// Soft-close commits no longer desired.
			foreach (var a in active.Where(a => !desiredSet.Contains(a.Sha)))
				await ctx.PlanNodeCommits
					.Where(c => c.NodeId == nid && c.Sha == a.Sha && c.ValidTo == null)
					.Set(c => c.ValidTo, _ => (DateTime?)now)
					.UpdateAsync(ct);

			// Insert newly desired commits.
			foreach (var sha in desiredSet.Where(s => !activeShas.Contains(s)))
				await ctx.InsertAsync(new PlanNodeCommit { NodeId = nid, Board = board, Sha = sha, ValidFrom = now }, token: ct);
		}
	}

	// Apply part_of (vertical decomposition) after the upsert. A patch whose PartOf is null
	// OMITS it (leave as-is); "" DETACHES (make a root); otherwise sets the parent (a slug
	// on this board or a NodeId). Enforces a single active parent and rejects cycles.
	public async Task SetPartOfAsync(
		string projectKey, string board, IReadOnlyList<NodePatch> patches, PlanNode[] desired, CancellationToken ct)
	{
		if (!patches.Any(p => p.PartOf is not null)) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		// Slug -> nodeId for parent resolution on this board: active rows overlaid with this batch.
		var slugToId = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.Where(n => n.NodeId.Length > 0).ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (k, nid) in byKey) slugToId[k] = nid;
		var parentOf = await ParentMapAsync(projectKey, ct);

		foreach (var p in patches)
		{
			if (p.PartOf is null) continue;
			if (!byKey.TryGetValue(p.Key, out var childId) || childId.Length == 0) continue;

			// Single parent: close any existing part_of from this child first.
			if (parentOf.TryGetValue(childId, out var oldParent))
			{
				await _relations.CloseAsync(projectKey, "part_of", childId, oldParent, ct);
				parentOf.Remove(childId);
			}
			if (p.PartOf.Length == 0) continue; // detach only

			var parentId = ResolveParentId(p.PartOf, slugToId, ctx);
			if (TaskSearchFilter.InSubtree(parentId, childId, parentOf)) // parent is the child or its descendant → cycle
				throw new ArgumentException($"part_of would create a cycle (node '{p.Key}')");
			await _relations.CreateAsync(projectKey, "part_of", childId, parentId, ct);
			parentOf[childId] = parentId;
		}
	}

	// Apply supersedes after the upsert: the new node replaces another, which is moved to
	// its kind's terminal-cancel (obsoleted). A system effect (no approve gate), like the
	// Done effects. Self-supersede and a missing target are ignored.
	public async Task SetSupersedesAsync(
		string projectKey, string board, IReadOnlyList<NodePatch> patches, PlanNode[] desired,
		MethodologyRuntime runtime, CancellationToken ct)
	{
		if (!patches.Any(p => !string.IsNullOrWhiteSpace(p.Supersedes))) return;
		var byKey = desired.ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		using var ctx = _boards.NewEnsuredConnection(projectKey);
		var slugToId = ctx.PlanNodes.Where(n => n.Board == board && n.ActiveTo == null).ToList()
			.Where(n => n.NodeId.Length > 0).ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
		foreach (var (k, nid) in byKey) slugToId[k] = nid;

		foreach (var p in patches)
		{
			if (string.IsNullOrWhiteSpace(p.Supersedes)) continue;
			if (!byKey.TryGetValue(p.Key, out var newId) || newId.Length == 0) continue;
			var targetId = ResolveParentId(p.Supersedes, slugToId, ctx);
			if (targetId == newId) continue; // a node can't supersede itself
			await _relations.CreateAsync(projectKey, "supersedes", newId, targetId, ct);
			// Obsolete the superseded node: move it to its workflow's terminal-cancel status.
			await _effects.SetActiveNodeStatusAsync(projectKey, targetId, runtime,
				(wf, node, isTerminal) => isTerminal ? null : wf?.Statuses.FirstOrDefault(s => s.Kind == StatusKind.TerminalCancel)?.Slug, ct);
		}
	}

	// Normalize a commit set: trim, lowercase, dedupe, drop empties; reject a value that is
	// not hex or whose length is outside 7..40 (git short-sha floor .. full sha). Empty in →
	// empty set (an explicit clear). Same validation-error shape as tag normalization.
	public static HashSet<string> NormalizeCommits(IReadOnlyList<string>? commits)
	{
		var set = new HashSet<string>(StringComparer.Ordinal);
		if (commits is null) return set;
		foreach (var raw in commits)
		{
			if (string.IsNullOrWhiteSpace(raw)) continue;
			var sha = raw.Trim().ToLowerInvariant();
			if (sha.Length is < 7 or > 40 || !sha.All(Uri.IsHexDigit))
				throw new ArgumentException($"commit '{raw}' must be a hex commit id of 7..40 chars");
			set.Add(sha);
		}
		return set;
	}

	// Resolve a PartOf / Supersedes value to a NodeId: a slug on this board, else an existing
	// NodeId (cross-board allowed — the project file holds every board's nodes).
	static string ResolveParentId(string partOf, Dictionary<string, string> slugToId, TasksDb ctx)
	{
		var v = partOf.Trim();
		if (slugToId.TryGetValue(v.ToLowerInvariant(), out var bySlug)) return bySlug;
		if (ctx.PlanNodes.Any(n => n.ActiveTo == null && n.NodeId == v)) return v;
		throw new ArgumentException($"part_of parent '{partOf}' is neither a node key on this board nor a known NodeId");
	}

	// nodeId -> its active part_of parent nodeId (single parent). One query, project-wide.
	async Task<Dictionary<string, string>> ParentMapAsync(string projectKey, CancellationToken ct) =>
		(await _relations.ListByKindAsync(projectKey, "part_of", ct))
			.GroupBy(e => e.FromNodeId, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First().ToNodeId, StringComparer.Ordinal);
}
