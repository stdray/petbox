using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services.Search;

// Composable post-select filters for the unified tasks read. Pure: takes candidate hits +
// resolved criteria and returns the filtered set. Shared status-slug resolution is also here
// so GetAsync (single-kind) and SearchNodesAsync (cross-kind) apply the same soft rules.
//
// ENTITY PREDICATES (spec tasks-search-entity-predicates-under-commit). `under` (the part_of
// subtree — a graph only the tasks entity has) and `commit` (reverse commit lookup — a tasks-only
// attribute) are predicates the опорный слой (search_meta) CANNOT express: search_meta carries the
// StatusKind facet + the identity alias set, not the part_of edges or the commit trailers. So they
// are declared entity-specific and applied HERE, at the pipeline's RE-FILTER step, over the pool the
// опорный слой already selected. Their existence does NOT grant selecting PAST that layer: they only
// ever NARROW the already-selected/faceted candidates (a `Where`), never widen the pool nor reach a
// row the statusKind facet excluded. `status` (a slug predicate) and `keys` (soft addressing) ride
// the same re-filter step for the same reason.
public static class TaskSearchFilter
{
	// Apply every non-null predicate in criteria order (under → status → keys → commit). Every one
	// is a pure NARROWING `Where` over the incoming pool — the re-filter step, never a re-selection.
	public static List<TaskSearchHit> Apply(IEnumerable<TaskSearchHit> hits, TaskSearchCriteria criteria)
	{
		IEnumerable<TaskSearchHit> q = hits;
		if (criteria.UnderRoots is not null)
		{
			// Empty roots → nothing passes. Parent map is required for a non-null under filter
			// so InSubtree can walk; an empty ParentOf is valid (every node is a root-or-orphan).
			var parentOf = criteria.ParentOf ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);
			var roots = criteria.UnderRoots;
			q = q.Where(h => roots.Any(root => InSubtree(h.Node.NodeId, root, parentOf)));
		}
		if (criteria.StatusSlugs is not null)
		{
			var status = criteria.StatusSlugs;
			q = q.Where(h => status.Contains(h.Node.Status));
		}
		if (criteria.KeyNodeIds is not null)
		{
			var keys = criteria.KeyNodeIds;
			q = q.Where(h => keys.Contains(h.Node.NodeId));
		}
		if (criteria.CommitNodeIds is not null)
		{
			var carrying = criteria.CommitNodeIds;
			q = q.Where(h => carrying.Contains(h.Node.NodeId));
		}
		return q as List<TaskSearchHit> ?? q.ToList();
	}

	// True if `nodeId` is `rootId` or a part_of descendant of it (walk parents up to root).
	public static bool InSubtree(string nodeId, string rootId, IReadOnlyDictionary<string, string> parentOf)
	{
		var cur = nodeId; var guard = 0;
		while (true)
		{
			if (cur == rootId) return true;
			if (!parentOf.TryGetValue(cur, out var par) || guard++ >= 1000) return false;
			cur = par;
		}
	}

	// Soft status-filter against a known slug set: unknown slugs dropped; provided-but-all-
	// unknown → empty set (empty result); none provided → null (no filter).
	public static HashSet<string>? ResolveStatusSlugs(IReadOnlyList<string>? status, IEnumerable<string> knownSlugs)
	{
		if (status is null || status.Count == 0) return null;
		var known = knownSlugs as HashSet<string>
			?? knownSlugs.ToHashSet(StringComparer.OrdinalIgnoreCase);
		// Ensure case-insensitive even when the caller already passed a set with a different comparer.
		if (!Equals(known.Comparer, StringComparer.OrdinalIgnoreCase))
			known = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var anyProvided = false;
		foreach (var raw in status)
		{
			var s = (raw ?? "").Trim();
			if (s.Length == 0) continue;
			anyProvided = true;
			if (known.Contains(s)) set.Add(s);
		}
		return anyProvided ? set : null;
	}

	// Status filter spanning boards of several kinds: a slug is valid if ANY kind in scope
	// knows it. Kinds are stored slugs (preset- or definition-resolved).
	public static HashSet<string>? ResolveStatusAcross(
		IReadOnlyList<string>? status, MethodologyRuntime runtime, IEnumerable<string> kindSlugs)
	{
		if (status is null || status.Count == 0) return null;
		var known = kindSlugs.Select(runtime.KindName).Distinct(StringComparer.Ordinal)
			.SelectMany(runtime.Types).SelectMany(w => w.Statuses).Select(s => s.Slug);
		return ResolveStatusSlugs(status, known);
	}

	// Status filter for a single board kind (GetAsync).
	public static HashSet<string>? ResolveStatusForKind(
		IReadOnlyList<string>? status, MethodologyRuntime runtime, string? kindSlug)
	{
		if (status is null || status.Count == 0) return null;
		var known = runtime.Types(kindSlug).SelectMany(w => w.Statuses).Select(s => s.Slug);
		return ResolveStatusSlugs(status, known);
	}
}
