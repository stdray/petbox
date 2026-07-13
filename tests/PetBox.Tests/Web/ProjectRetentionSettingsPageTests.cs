using Microsoft.AspNetCore.Mvc;
using PetBox.Web;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// Log retention used to live in a bespoke control on the project Info page (/info) — card
// ui-log-retention-settings-fix. admin-routes-and-pages item 3 moved it to the generic project
// Settings page (/settings): LogSettings.RetentionDays is already in SettingsScopePolicy.Records, so
// it renders there through the same engine as every other cascading setting instead of a hand-rolled
// hint UI. The hint-specific behavior (Hint_NoOverride_UsesSystemDefault /
// Hint_ActiveOverride_ExposesOverrideAndTrueDefaultSeparately) tested ProjectDetailModel properties
// that no longer exist — the generic-page coverage for RetentionDays now lives in
// SettingsFormScopePagesTests (ProjectSettingsAdmin_AlsoShowsLogIngestionDashboardFields_UnderInterimB
// and friends). What's left to lock here is just the redirect: the project-scope /log page had no
// fields, reported a false "saved", and re-POSTed on refresh; it stays a pure GET-only redirect, now
// to /settings instead of /info.
public sealed class ProjectRetentionSettingsPageTests
{
	const string Ws = "ws";
	const string Proj = "proj";

	[Fact]
	public void LogPage_Get_RedirectsToSettings_NoFormNoFalseSuccess()
	{
		var page = new ProjectLogSettingsModel { WorkspaceKey = Ws, ProjectKey = Proj };

		var result = page.OnGet();

		var redirect = result.Should().BeOfType<RedirectResult>().Subject;
		redirect.Url.Should().Be(Routes.ProjectSettingsAdmin(Ws, Proj));
		redirect.Url.Should().EndWith("/settings");
	}

	[Fact]
	public void LogPage_HasNoSaveHandler()
	{
		// The empty project-scope form and its false "Log settings saved." no-op POST are gone.
		typeof(ProjectLogSettingsModel).GetMethod("OnPostSaveAsync").Should().BeNull();
		typeof(ProjectLogSettingsModel).GetMethod("OnPostSave").Should().BeNull();
	}

	[Fact]
	public void ProjectDetailModel_NoLongerExposesRetentionProperties()
	{
		// Locks the move: retention properties are gone from the Info page model — a regression here
		// would mean the bespoke control crept back in alongside the generic one.
		var t = typeof(ProjectDetailModel);
		t.GetProperty("EffectiveRetentionDays").Should().BeNull();
		t.GetProperty("RetentionOverrideDays").Should().BeNull();
		t.GetProperty("DefaultRetentionDays").Should().BeNull();
		t.GetMethod("OnPostSetRetentionAsync").Should().BeNull();
		t.GetMethod("OnPostClearRetentionAsync").Should().BeNull();
	}
}
