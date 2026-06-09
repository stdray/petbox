using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// FTS5 mirror of the active, non-terminal plan nodes across every board of a project,
// rebuilt on every upsert. Not temporal — it only ever holds the current open set,
// keyed by the stable NodeId (UNINDEXED in the virtual table; Name/Body/Tags are the
// indexed columns matched against). Board is UNINDEXED so a search can scope to one
// board (the project file holds every board's nodes, partitioned by Board).
[Table("plan_nodes_fts")]
public sealed class PlanNodeFts
{
	[Column] public string NodeId { get; set; } = string.Empty;
	[Column] public string Board { get; set; } = string.Empty;
	[Column] public string Name { get; set; } = string.Empty;
	[Column] public string Body { get; set; } = string.Empty;
	[Column] public string Tags { get; set; } = string.Empty;
}
