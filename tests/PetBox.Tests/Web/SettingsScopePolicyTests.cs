using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// INTERIM decision B (idea settings-leaf-override-uniform, spec settings-uniform-override): every
// bit of B's behavior — the uniform Records registry AND the editable-at-scope rule — is
// concentrated in SettingsScopePolicy, the single seam this suite locks down. When B is replaced by
// the real constraint model (idea settings-scope-effective-constraints), this file (and the policy
// type) is the one thing that needs to change.
public sealed class SettingsScopePolicyTests
{
	[Fact]
	public void Records_IsTheUniformRegistry_ExcludingRepoSettings()
	{
		SettingsScopePolicy.Records.Should().BeEquivalentTo(
		[
			typeof(LogSettings),
			typeof(IngestionSettings),
			typeof(DashboardSettings),
			typeof(SessionFullScanSettings),
		]);
	}

	[Theory]
	[InlineData(Scope.System)]
	[InlineData(Scope.Workspace)]
	[InlineData(Scope.Project)]
	public void IsRecordVisibleAt_LogSettings_VisibleAtAllThreeUniformScopes(Scope scope) =>
		SettingsScopePolicy.IsRecordVisibleAt(typeof(LogSettings), scope).Should().BeTrue();

	[Fact]
	public void IsRecordVisibleAt_SessionFullScanSettings_OnlyAtItsOwnPinnedScopes()
	{
		SettingsScopePolicy.IsRecordVisibleAt(typeof(SessionFullScanSettings), Scope.System).Should().BeTrue();
		SettingsScopePolicy.IsRecordVisibleAt(typeof(SessionFullScanSettings), Scope.Project).Should().BeTrue();
		SettingsScopePolicy.IsRecordVisibleAt(typeof(SessionFullScanSettings), Scope.Workspace).Should().BeFalse();
	}

	[Fact]
	public void IsEditableAt_NonPinnedField_TrueAcrossSystemWorkspaceProject_RegardlessOfTopLevel()
	{
		// IngestionSettings.ChannelCapacity has TopLevel=System — B ignores that ceiling for the
		// three uniform scopes.
		var attr = typeof(IngestionSettings).GetProperty(nameof(IngestionSettings.ChannelCapacity))!
			.GetCustomAttributes(typeof(SettingAttribute), inherit: false)
			.Cast<SettingAttribute>().Single();

		SettingsScopePolicy.IsEditableAt(attr, Scope.System).Should().BeTrue();
		SettingsScopePolicy.IsEditableAt(attr, Scope.Workspace).Should().BeTrue();
		SettingsScopePolicy.IsEditableAt(attr, Scope.Project).Should().BeTrue();
	}

	[Fact]
	public void IsEditableAt_OutsideUniformRange_KeepsOriginalTopLevelCeiling()
	{
		// UiSettings.Theme (TopLevel=User) at Scope.User — untouched by B (Preferences page).
		var attr = typeof(UiSettings).GetProperty(nameof(UiSettings.Theme))!
			.GetCustomAttributes(typeof(SettingAttribute), inherit: false)
			.Cast<SettingAttribute>().Single();

		SettingsScopePolicy.IsEditableAt(attr, Scope.User).Should().BeTrue();
	}

	[Fact]
	public void IsEditableAt_HasMinScope_WinsOverTheUniformCeilingLift()
	{
		var systemEnabled = typeof(SessionFullScanSettings).GetProperty(nameof(SessionFullScanSettings.SystemEnabled))!
			.GetCustomAttributes(typeof(SettingAttribute), inherit: false)
			.Cast<SettingAttribute>().Single();
		var projectEnabled = typeof(SessionFullScanSettings).GetProperty(nameof(SessionFullScanSettings.ProjectEnabled))!
			.GetCustomAttributes(typeof(SettingAttribute), inherit: false)
			.Cast<SettingAttribute>().Single();

		SettingsScopePolicy.IsEditableAt(systemEnabled, Scope.System).Should().BeTrue();
		SettingsScopePolicy.IsEditableAt(systemEnabled, Scope.Workspace).Should().BeFalse();
		SettingsScopePolicy.IsEditableAt(systemEnabled, Scope.Project).Should().BeFalse();

		SettingsScopePolicy.IsEditableAt(projectEnabled, Scope.Project).Should().BeTrue();
		SettingsScopePolicy.IsEditableAt(projectEnabled, Scope.System).Should().BeFalse();
		SettingsScopePolicy.IsEditableAt(projectEnabled, Scope.Workspace).Should().BeFalse();
	}
}
