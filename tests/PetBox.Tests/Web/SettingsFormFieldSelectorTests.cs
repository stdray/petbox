using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// Card project-log-settings-empty-form. Root cause: _SettingsForm.cshtml / _SettingsFormFields.cshtml
// filtered fields with `if ((int)Model.CurrentScope > (int)attr.TopLevel) continue;` — a WRITE-DEPTH
// read of a field meant as a CASCADE-READ ceiling. On the project-scope /log page (Scope.Project = 2)
// this filtered out every LogSettings property (all TopLevel <= Workspace), so the page rendered with
// zero fields but still kept a bare Save button. That specific page was already fixed in commit
// 9b0d4cd by removing project-scope LogSettings from the generic form entirely (a bespoke control on
// the project Info page instead). This test locks the SHARED filtering logic itself — now extracted
// into SettingsFormFieldSelector.GetEditable so both partials can't drift — and the historical
// project-scope repro is kept here as a regression lock even though no live page exercises it anymore.
public sealed class SettingsFormFieldSelectorTests
{
	[Fact]
	public void LogSettings_AtProjectScope_HasNoEditableFields_TheHistoricalRepro()
	{
		// This is the exact combination that used to render an empty _SettingsForm with a bare
		// Save button on /ui/admin/ws/{ws}/projects/{key}/log before commit 9b0d4cd. No current page
		// calls GetEditable(LogSettings, Project) directly, but the filtering primitive must still
		// report "empty" here — any FUTURE caller that hits this combination needs its emptiness
		// surfaced (as an explanatory empty state), not hidden behind a lone Save button.
		var visible = SettingsFormFieldSelector.GetEditable(typeof(LogSettings), Scope.Project);

		visible.Should().BeEmpty();
	}

	[Fact]
	public void LogSettings_AtWorkspaceScope_ShowsOnlyRetentionDays()
	{
		var visible = SettingsFormFieldSelector.GetEditable(typeof(LogSettings), Scope.Workspace);

		visible.Select(v => v.Property.Name).Should().BeEquivalentTo([nameof(LogSettings.RetentionDays)]);
	}

	[Fact]
	public void LogSettings_AtSystemScope_ShowsAllThreeFields()
	{
		var visible = SettingsFormFieldSelector.GetEditable(typeof(LogSettings), Scope.System);

		visible.Select(v => v.Property.Name).Should().BeEquivalentTo(
		[
			nameof(LogSettings.RetentionDays),
			nameof(LogSettings.SystemRetainDays),
			nameof(LogSettings.RunIntervalSeconds),
		]);
	}

	[Fact]
	public void UiSettings_AtUserScope_ShowsTheme_TheOnlyLive_SettingsForm_Caller()
	{
		// Pages/Me/Preferences.cshtml is the only current caller of _SettingsForm (full form incl.
		// Save button) — this must stay non-empty or Preferences would hit the same bug class.
		var visible = SettingsFormFieldSelector.GetEditable(typeof(UiSettings), Scope.User);

		visible.Select(v => v.Property.Name).Should().BeEquivalentTo([nameof(UiSettings.Theme)]);
	}

	[Fact]
	public void IngestionSettings_And_DashboardSettings_AtSystemScope_AreNonEmpty()
	{
		// The other two records shown on the Sys Defaults page — must stay populated at System scope.
		SettingsFormFieldSelector.GetEditable(typeof(IngestionSettings), Scope.System).Should().NotBeEmpty();
		SettingsFormFieldSelector.GetEditable(typeof(DashboardSettings), Scope.System).Should().NotBeEmpty();
	}
}
