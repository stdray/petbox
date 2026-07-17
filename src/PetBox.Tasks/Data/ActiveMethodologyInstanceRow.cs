using LinqToDB.Mapping;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tasks.Data;

// The project's explicit "which methodology instance is active" pointer (spec
// methodology-active-instance): controls DEFAULTS only (UI, MCP verbs called without an
// explicit instance, tasks_methodology_guide with no `name`) — NEVER board membership
// rules, which always resolve through TaskBoards.MethodologyInstance regardless of what is
// active here. Multi-key temporal (SCD type-2) SINGLETON row in the per-project tasks file,
// same one-key shape as MethodologyDefRow: Key is the fixed SingletonKey, InstanceName is
// the pointed-at instance's name. The pointer MUST reference an OPEN instance — enforced at
// write time (MethodologyInstanceService.SetActiveAsync); a stale pointer (the pointed
// instance closed after being set) is treated as absent by the read-side resolver
// (ResolveActiveNameAsync), not blindly followed. Absence of an active row (never set, or
// cleared) is a legal, common state — resolution then falls back to the single-open-instance
// case (unambiguous without a pointer) or an explicit "no default" state (never a silent
// merge across several open instances).
[Table("methodology_active_instance")]
public sealed record ActiveMethodologyInstanceRow : TemporalRow
{
	public const string SingletonKey = "active";

	[Column, NotNull] public string InstanceName { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is ActiveMethodologyInstanceRow a && a.InstanceName == InstanceName;

	public override IReadOnlyList<string> ChangedPayloadFields(TemporalRow other) =>
		other is ActiveMethodologyInstanceRow a && a.InstanceName != InstanceName ? ["instanceName"] : [];

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
