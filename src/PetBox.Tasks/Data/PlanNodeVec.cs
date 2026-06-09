using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// Vector mirror of the active, non-terminal plan nodes for semantic board search: one
// embedding per open node, keyed by the stable NodeId, tagged with the producing
// embedding model + dimension alongside the packed float32 BLOB so the query path can
// model/dim-guard candidates (only fuse rows embedded by the same model at the same dim
// as the query). Board is carried so a search can scope to one board. Written on upsert
// when an LLM embed capability is available; absent rows simply mean lexical-only.
[Table("plan_node_vec")]
public sealed class PlanNodeVec
{
	[Column, PrimaryKey] public string NodeId { get; set; } = string.Empty;
	[Column] public string Board { get; set; } = string.Empty;
	[Column] public string Model { get; set; } = string.Empty;
	[Column] public int Dim { get; set; }
	[Column] public byte[] Vec { get; set; } = [];
}
