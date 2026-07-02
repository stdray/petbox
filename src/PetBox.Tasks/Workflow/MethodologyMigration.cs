namespace PetBox.Tasks.Workflow;

// The `migration` document of tasks.methodology_def_upsert (spec primitives-schema-
// migration): per board-kind slug, DECLARED value mappings that repair live nodes a
// definition change would otherwise strand. A mapping applies ONLY where a node's current
// value is INVALID under the new resolution — a valid value is never rewritten, so this is
// declarative repair, not bulk rename. Per node the order is: map the type first (when the
// type no longer resolves), then resolve the (possibly mapped) type's new workflow, then
// map the status if it is unknown to that workflow. Anything still incompatible after the
// mappings rejects the whole def_upsert — nothing is written.
public sealed record MethodologyMigration(
	string Kind,
	IReadOnlyList<MethodologyValueMap> Types,
	IReadOnlyList<MethodologyValueMap> Statuses);

// One declared value repair: a node whose current type/status equals `From`
// (case-insensitive) and is invalid under the new resolution is rewritten to `To`.
public sealed record MethodologyValueMap(string From, string To);
