using Microsoft.AspNetCore.Authorization;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "SysAdmin")]
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
