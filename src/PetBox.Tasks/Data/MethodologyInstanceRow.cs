using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// A NAMED methodology INSTANCE — the live process automaton (boards + rules + open/closed).
// Multi-key temporal (SCD type-2) in the per-project tasks file: Key is the instance name
// (slug), Json is the MethodologyDefinition rules (camelCase, enums as strings — same wire
// as def/template), ClosedAt null = open. Board membership is on TaskBoards.MethodologyInstance
// (Core catalog); this row owns identity + rules + status only.
[Table("methodology_instances")]
public sealed record MethodologyInstanceRow : TemporalRow
{
	[Column, NotNull] public string Json { get; init; } = string.Empty;

	// null = open; set = closed (history readable, no new work on member boards).
	[Column, Nullable] public DateTime? ClosedAt { get; init; }

	public override bool SamePayload(TemporalRow other) =>
		other is MethodologyInstanceRow m && m.Json == Json && m.ClosedAt == ClosedAt;

	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other)
	{
		if (other is not MethodologyInstanceRow m) return [];
		var fields = new List<string>(2);
		if (m.Json != Json) fields.Add("definition");
		if (m.ClosedAt != ClosedAt) fields.Add("closedAt");
		return fields;
	}

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
