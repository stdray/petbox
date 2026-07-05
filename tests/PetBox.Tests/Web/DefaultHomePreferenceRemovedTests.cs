using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web;
using PetBox.Web.Navigation;
using PetBox.Web.Pages;
using PetBox.Web.Pages.Me;

namespace PetBox.Tests.Web;

// Card ui-defaulthome-phantom. The DefaultHome preference (Status/LastProject/AllLogs) was a
// phantom: the app root always redirected to workspace status, so LastProject/AllLogs were dead
// no-ops. The setting is removed entirely. This locks:
//   1. Preferences no longer offers a DefaultHome control (the setting is gone from UiSettings, so
//      the reflection-driven _SettingsForm can't render it) and the Save handler no longer binds it.
//   2. The orphaned DefaultHome enum type is gone.
//   3. The app root still redirects to the current workspace status page — unchanged behavior.
public sealed class DefaultHomePreferenceRemovedTests
{
	[Fact]
	public void UiSettings_HasNoDefaultHomeSetting()
	{
		typeof(UiSettings).GetProperty("DefaultHome").Should().BeNull(
			"the phantom DefaultHome preference must be removed");

		// The form + resolver reflect over [Setting]-annotated properties; none may key the dead path.
		var settingKeys = typeof(UiSettings)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(p => p.GetCustomAttribute<SettingAttribute>())
			.Where(a => a is not null)
			.Select(a => a!.Key);
		settingKeys.Should().NotContain("ui.defaultHome");
	}

	[Fact]
	public void DefaultHomeEnum_IsGone()
	{
		typeof(UiSettings).Assembly.GetType("PetBox.Core.Settings.DefaultHome")
			.Should().BeNull("the orphaned DefaultHome enum must be removed");
	}

	[Fact]
	public void PreferencesSaveHandler_BindsOnlyTheme()
	{
		var handler = typeof(PreferencesModel).GetMethod("OnPostSaveAsync");
		handler.Should().NotBeNull();
		handler!.GetParameters().Select(p => p.Name).Should().Equal("Theme");
	}

	[Fact]
	public void Root_RedirectsToWorkspaceStatus()
	{
		var page = new IndexModel(new FakeNav("acme"));

		var result = page.OnGet();

		var redirect = result.Should().BeOfType<RedirectResult>().Subject;
		redirect.Url.Should().Be(Routes.Workspace("acme"));
	}

	// IndexModel only reads CurrentWorkspaceKey; the rest is inert.
	sealed class FakeNav(string ws) : INavigationContext
	{
		public bool IsAuthenticated => true;
		public string? Username => null;
		public string CurrentWorkspaceKey => ws;
		public string? CurrentProjectKey => null;
		public IReadOnlyList<WorkspaceOption> AvailableWorkspaces => [];
		public IReadOnlyList<Project> ProjectsInCurrentWorkspace => [];
		public IReadOnlyDictionary<string, IReadOnlyList<Project>> ProjectsByWorkspace =>
			new Dictionary<string, IReadOnlyList<Project>>();
		public bool DataEnabled => false;
		public bool TasksEnabled => false;
		public bool MemoryEnabled => false;
		public bool LlmRouterEnabled => false;
	}
}
