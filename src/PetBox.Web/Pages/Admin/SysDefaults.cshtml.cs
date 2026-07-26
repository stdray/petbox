using Microsoft.AspNetCore.Authorization;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "SysAdmin")]
// Scope.System settings, at the fixed ScopeKey "$" below — the TOP of the settings cascade, whose
// values every workspace and project inherits until one overrides them. The scope key is a constant on
// this class, not something the request can name, so there is no tenant to read and nothing a caller
// could aim: the workspace and project rungs of the same cascade are Admin/WorkspaceDefaults and
// Admin/ProjectSettingsAdmin, which DO carry their tenant in the route and declare it.
[TenantExempt(TenantExemption.FleetWide,
	"the system rung of the settings cascade (Scope.System, ScopeKey \"$\" — a constant on this page); "
	+ "it defaults every tenant at once and is named by none")]
public sealed class SysDefaultsModel : SettingsScopePageModel
{
	public SysDefaultsModel(ISettingsResolver resolver) : base(resolver) { }

	protected override Scope Scope => Scope.System;
	protected override string ScopeKey => "$";

	// Uniform registry (INTERIM decision B — see SettingsScopePolicy): same list on all three
	// generic scope pages. SettingsScopePolicy.IsRecordVisibleAt/IsEditableAt decide what's
	// actually shown at this page's scope.
	protected override IReadOnlyList<Type> Records => SettingsScopePolicy.Records;
}
