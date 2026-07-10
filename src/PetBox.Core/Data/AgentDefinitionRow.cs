using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Core.Data;

// Portable agent-definition document (agent-definition-as-data): multi-key temporal
// (SCD type-2) in the main Core DB, partitioned by ProjectKey. Key is the definition
// slug (e.g. "default"); payload is the whole roster JSON (camelCase) — roles with
// tier/capabilities/spawn/escalation, NO model fields (model binding is local).
// Partition mirrors plan_nodes.Board: version cursor and active-key uniqueness are
// per project so two projects may both own key "default".
[Table("agent_definitions")]
public sealed record AgentDefinitionRow : TemporalRow
{
	// Partition identity — not payload (SamePayload ignores it).
	[Column, NotNull] public string ProjectKey { get; init; } = string.Empty;

	[Column, NotNull] public string Json { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is AgentDefinitionRow a && a.Json == Json;

	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other) =>
		other is AgentDefinitionRow a && a.Json != Json ? ["definition"] : [];

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
