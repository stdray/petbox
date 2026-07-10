using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// A NAMED methodology template — a reusable MethodologyDefinition document independent of
// running process boards / instances (spec methodology-template). Multi-key temporal
// (SCD type-2) in the per-project tasks file: Key is the template slug (not the singleton
// "methodology" of MethodologyDefRow), payload is the whole definition as JSON (camelCase,
// enums as strings — same wire as def_get/def_upsert). Template write never provisions
// boards or rewrites live nodes; that is instance create / def_upsert territory.
//
// Compat dual-read: the legacy singleton definition (MethodologyDefRow) is still readable
// via def_get AND surfaces as a virtual template under key MethodologyDefRow.SingletonKey
// with source="definition" when no STORED template owns that key — see
// MethodologyTemplateService. Builtin presets (quartet|classic|simple) are virtual too
// (source="builtin"); they are never rows in this table.
[Table("methodology_templates")]
public sealed record MethodologyTemplateRow : TemporalRow
{
	[Column, NotNull] public string Json { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is MethodologyTemplateRow m && m.Json == Json;

	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other) =>
		other is MethodologyTemplateRow m && m.Json != Json ? ["definition"] : [];

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
