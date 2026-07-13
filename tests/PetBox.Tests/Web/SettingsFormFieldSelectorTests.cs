using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// Card project-log-settings-empty-form + INTERIM decision B (idea settings-leaf-override-uniform,
// spec settings-uniform-override): all B policy lives in SettingsScopePolicy — GetEditable here is
// just the reflection walk that delegates to SettingsScopePolicy.IsEditableAt per property. B lifts
// the old "TopLevel is a write-depth ceiling" rule for System/Workspace/Project (every field of a
// non-HasMinScope record is now editable at any of the three), while Service/User/Membership
// (Preferences is the only live page there) keep the original ceiling untouched, and HasMinScope
// pins (SessionFullScanSettings) still win over everything.
public sealed class SettingsFormFieldSelectorTests
{
	[Fact]
	public void LogSettings_AtProjectScope_NowEditable_UnderInterimB()
	{
		// Before INTERIM decision B this combination used to render an empty _SettingsForm with a
		// bare Save button on /ui/admin/ws/{ws}/projects/{key}/log (fixed in commit 9b0d4cd by
		// dropping project-scope LogSettings from the generic form entirely). B's ceiling-lift now
		// makes every LogSettings field editable at Project scope too — ProjectSettingsAdmin relies
		// on exactly this to include LogSettings in its uniform Records set.
		var visible = SettingsFormFieldSelector.GetEditable(typeof(LogSettings), Scope.Project);

		visible.Select(v => v.Property.Name).Should().BeEquivalentTo(
		[
			nameof(LogSettings.RetentionDays),
			nameof(LogSettings.SystemRetainDays),
			nameof(LogSettings.RunIntervalSeconds),
		]);
	}

	[Fact]
	public void LogSettings_AtWorkspaceScope_ShowsAllThreeFields_UnderInterimB()
	{
		// Pre-B this only showed RetentionDays (TopLevel=Workspace); B lifts the ceiling for
		// System/Workspace/Project uniformly, so the System-only fields now show here too.
		var visible = SettingsFormFieldSelector.GetEditable(typeof(LogSettings), Scope.Workspace);

		visible.Select(v => v.Property.Name).Should().BeEquivalentTo(
		[
			nameof(LogSettings.RetentionDays),
			nameof(LogSettings.SystemRetainDays),
			nameof(LogSettings.RunIntervalSeconds),
		]);
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
	public void BrowserState_AtUserScope_ShowsTheme_TheOnlyLive_SettingsForm_Caller()
	{
		// Pages/Me/Preferences.cshtml is the only current caller of _SettingsForm (full form incl.
		// Save button), at Scope.User — outside B's System/Workspace/Project range, so this keeps the
		// original TopLevel-ceiling behavior untouched. BrowserState also carries SidebarPinned, but
		// that's [BrowserState]-tagged (cookie branch), not [Setting] — GetEditable only walks
		// [Setting] properties, so Theme is still the only field this page renders (work
		// `ui-state-theme-unify` folded Theme in from the retired UiSettings).
		var visible = SettingsFormFieldSelector.GetEditable(typeof(BrowserState), Scope.User);

		visible.Select(v => v.Property.Name).Should().BeEquivalentTo([nameof(BrowserState.Theme)]);
	}

	[Fact]
	public void IngestionSettings_And_DashboardSettings_AreNonEmpty_AtEveryUniformScope()
	{
		// Under B these System-only-TopLevel records are now editable (hence project-overridable) at
		// all three generic scope pages.
		foreach (var scope in new[] { Scope.System, Scope.Workspace, Scope.Project })
		{
			SettingsFormFieldSelector.GetEditable(typeof(IngestionSettings), scope).Should().NotBeEmpty();
			SettingsFormFieldSelector.GetEditable(typeof(DashboardSettings), scope).Should().NotBeEmpty();
		}
	}

	[Fact]
	public void SessionFullScanSettings_HasMinScope_PinsEachFieldToItsOwnScope_EvenUnderInterimB()
	{
		// The one exception B still honors: two independent, non-cascading switches, each pinned to
		// exactly one scope (see SettingAttribute.HasMinScope) — B's uniform ceiling-lift must not
		// resurrect the dead-write bug this pin exists to prevent.
		SettingsFormFieldSelector.GetEditable(typeof(SessionFullScanSettings), Scope.System)
			.Select(v => v.Property.Name).Should().BeEquivalentTo([nameof(SessionFullScanSettings.SystemEnabled)]);

		SettingsFormFieldSelector.GetEditable(typeof(SessionFullScanSettings), Scope.Project)
			.Select(v => v.Property.Name).Should().BeEquivalentTo([nameof(SessionFullScanSettings.ProjectEnabled)]);

		SettingsFormFieldSelector.GetEditable(typeof(SessionFullScanSettings), Scope.Workspace)
			.Should().BeEmpty();
	}
}
