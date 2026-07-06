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

	// Any record with at least one [Setting] whose TopLevel >= System belongs here; the form
	// renderer hides properties whose TopLevel < System.
	protected override IReadOnlyList<Type> Records =>
	[
		typeof(LogSettings),
		typeof(IngestionSettings),
		typeof(DashboardSettings),
		typeof(SessionFullScanSettings),
	];
}
