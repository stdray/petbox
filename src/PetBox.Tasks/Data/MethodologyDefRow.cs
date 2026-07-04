using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// The project's user-defined methodology definition, stored as a temporal (SCD type-2)
// row in the per-project tasks file (next to plan_nodes/comments). ONE definition per
// project: Key is the fixed SingletonKey, so the row set is the definition's revision
// history and the active revision (ActiveTo IS NULL) is the current methodology. The
// payload is the whole MethodologyDefinition serialized to JSON (camelCase, enums as
// strings) — the engine (next task) deserializes it to resolve kinds/FSMs; this slice
// only stores and validates.
[Table("methodology_defs")]
public sealed record MethodologyDefRow : TemporalRow
{
	// The one Key every project's definition lives under (singleton per tasks file).
	public const string SingletonKey = "methodology";

	[Column, NotNull] public string Json { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is MethodologyDefRow m && m.Json == Json;

	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other) =>
		other is MethodologyDefRow m && m.Json != Json ? ["definition"] : [];

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
