using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// A typed directed edge between two node ids (stable PlanNode.NodeId values).
// Project-level (lives in petbox.db), references node ids LOGICALLY — boards are
// separate files, so there's no cross-file FK; NodeId is globally unique. Edges
// survive node renames because they bind to NodeId, not Key.
//   Kind ∈ task_spec | issue_task | idea_spec | blocks | nfr | dup
[Table("Relation")]
public sealed record Relation
{
	[Column, PrimaryKey, NotNull] public string Id { get; init; } = string.Empty;
	[Column, NotNull] public string ProjectKey { get; init; } = string.Empty;
	[Column, NotNull] public string Kind { get; init; } = string.Empty;
	[Column, NotNull] public string FromNodeId { get; init; } = string.Empty;
	[Column, NotNull] public string ToNodeId { get; init; } = string.Empty;
	[Column, NotNull] public DateTime CreatedAt { get; init; }
}
