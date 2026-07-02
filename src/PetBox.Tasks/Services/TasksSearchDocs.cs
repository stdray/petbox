using PetBox.Core.Search;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Services;

// Single source of truth for how a plan node maps onto the entity-addressed search contract.
// Tasks search covers only the OPEN set (active, non-terminal) — terminal/closed nodes are dropped
// from the index, so membership is the IsIndexable predicate. Entity address: Scope=projectKey,
// Type=Board (so a board filter is a SearchFilter(Type=board) and the per-board vector cursor uses
// IndexName=Board), Id=node slug (the temporal Key) — the temporal log's slugs map straight through,
// so renames/soft-deletes address the right row without needing a closed node's NodeId.
public static class TasksSearchDocs
{
	// Indexed iff the node has a stable identity and is not in a terminal workflow state.
	// The runtime overload also recognizes a project definition's terminal statuses; the
	// bare form is the presets-only view (background board walkers without a runtime).
	public static bool IsIndexable(PlanNode n) => IsIndexable(n, MethodologyRuntime.PresetsOnly);

	public static bool IsIndexable(PlanNode n, MethodologyRuntime runtime) =>
		n.NodeId.Length > 0 && !runtime.IsTerminalSlug(n.Status);

	public static SearchDoc ToDoc(PlanNode n, string scope, IReadOnlyList<string> tags) =>
		new(scope, n.Board, n.Key, n.Name + "\n" + n.Body, string.Join(' ', tags));
}
