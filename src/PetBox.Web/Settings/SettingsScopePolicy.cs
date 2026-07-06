using System.Reflection;
using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// INTERIM decision B (idea settings-leaf-override-uniform, spec settings-uniform-override): "the
// leaf overrides everything" — every [Setting] record shown on the three generic scope pages
// (Sys/Workspace/Project Settings) is editable at ANY of System/Workspace/Project, deeper-wins on
// read via the (untouched) SettingsResolver cascade. This is a deliberate, temporary widening of
// SettingAttribute.TopLevel's original "write-depth ceiling" meaning — kept ONLY for this policy;
// scopes outside System/Workspace/Project (Service/User/Membership — e.g. Me/Preferences) keep the
// original TopLevel-is-a-ceiling behavior untouched, and SettingsResolver's own read-cascade depth
// (BuildChainAsync) is not touched either.
//
// EVERYTHING B decides lives in this one type on purpose: when the real answer (idea
// settings-scope-effective-constraints — shallower scopes CONSTRAIN deeper ones, not just default
// them) replaces this interim, there is exactly one seam to swap, not scattered call sites across
// SettingsFormFieldSelector + three page models + a records registry.
public static class SettingsScopePolicy
{
	// The single, uniform settings-record registry for the three generic scope pages
	// (SysDefaultsModel / WorkspaceDefaultsModel / ProjectSettingsAdminModel) — one list, so a new
	// record type is automatically offered at all three levels. Deliberately NOT RepoSettings:
	// CommitUrlTemplate already has a bespoke control on ProjectDetail.cshtml (project Info page);
	// adding it here would give it two disagreeing edit surfaces.
	public static readonly IReadOnlyList<Type> Records =
	[
		typeof(LogSettings),
		typeof(IngestionSettings),
		typeof(DashboardSettings),
		typeof(SessionFullScanSettings),
	];

	// The scope range this interim policy governs. Service/User/Membership pages (just
	// Me/Preferences today, at Scope.User) are deliberately left on the original semantics below.
	static bool IsUniformScope(Scope scope) => scope is Scope.System or Scope.Workspace or Scope.Project;

	// Is `attr`'s property editable at `currentScope`?
	//
	// - HasMinScope always wins first, uniform scope or not: it's a bespoke AND-gate/pin (e.g.
	//   SessionFullScanSettings' two independent, non-cascading switches), not a cascade, and B's
	//   uniform widening must not resurrect the dead-write bug it was added to prevent (see
	//   SettingAttribute.HasMinScope).
	// - Otherwise, at System/Workspace/Project every field is editable regardless of TopLevel — B's
	//   ceiling-lift.
	// - Outside that range (Service/User/Membership), TopLevel is still the write-depth ceiling
	//   (original, pre-B semantics): editable only where currentScope is no finer than TopLevel.
	public static bool IsEditableAt(SettingAttribute attr, Scope currentScope)
	{
		if (attr.HasMinScope) return currentScope == attr.MinScope;
		if (IsUniformScope(currentScope)) return true;
		return (int)currentScope <= (int)attr.TopLevel;
	}

	// Does `recordType` have at least one property editable at `scope`? Backs the generic pages'
	// section threshold — replaces the old per-record `TopLevel >= Scope` check in
	// SettingsScopePageModel.LoadSectionsAsync so a record with every field pinned away from this
	// scope (HasMinScope) is correctly hidden rather than rendered as an empty form.
	public static bool IsRecordVisibleAt(Type recordType, Scope scope) =>
		recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => p.GetCustomAttribute<SettingAttribute>())
			.Any(a => a is not null && IsEditableAt(a, scope));
}
