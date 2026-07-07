using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// Focus coverage for the sidebar project selector (_ZoneSelectors.cshtml):
// [data-testid=nav-project-select] is an onchange-submit form posting to /api/ui/project.
// Choosing a project must navigate to that project's zone (/ui/{ws}/{key}) and the sidebar
// must re-render in the new project's context (hidden marker nav-project carries the key).
[Collection(nameof(UiCollection))]
public sealed class NavProjectSelectorTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = TestWorkspace.Key;
	const string ProjA = "navsel-a";
	const string ProjB = "navsel-b";

	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task SelectingProject_NavigatesAndRerendersSidebarContext()
	{
		// Two projects in the same workspace so the selector holds a real choice.
		await EnsureProject(ProjA, "NavSel A");
		await EnsureProject(ProjB, "NavSel B");

		// Land on ProjA so it is the current project, then drop to the workspace landing
		// (no project in the URL) where the sidebar selector lists both projects.
		await _page!.GotoAsync($"/ui/{Ws}/{ProjA}");
		await _page.GotoAsync($"/ui/{Ws}");

		var select = _page.GetByTestId("nav-project-select");
		await Expect(select.Locator($"option[value=\"{ProjA}\"]")).ToHaveCountAsync(1);
		await Expect(select.Locator($"option[value=\"{ProjB}\"]")).ToHaveCountAsync(1);

		// Choose the OTHER project via the selector — onchange submits the switch form.
		await select.SelectOptionAsync(new SelectOptionValue { Value = ProjB });

		// Navigation lands on the chosen project's zone.
		await _page.WaitForURLAsync($"**/ui/{Ws}/{ProjB}");
		_page.Url.Should().Contain($"/ui/{Ws}/{ProjB}");

		// Sidebar re-rendered in the chosen project's context.
		await Expect(_page.GetByTestId("nav-project")).ToHaveAttributeAsync("data-project-key", ProjB);
	}

	// Zone-preserving switch: the same selector, but rendered on an ADMIN page, must keep the
	// user inside the admin zone of the newly-chosen project instead of dropping them into the
	// /ui dashboard. _ZoneSelectors sets its hidden `zone` field to "admin" under /ui/admin/...,
	// and ProjectSwitchEndpoint honours it by redirecting to the project's admin Info page.
	[Fact]
	public async Task SelectingProject_OnAdminPage_StaysInAdminZone()
	{
		await EnsureProject(ProjA, "NavSel A");
		await EnsureProject(ProjB, "NavSel B");

		// Land on ProjA's admin Info page, where _ZoneSelectors renders with zone=admin.
		await _page!.GotoAsync($"/ui/admin/ws/{Ws}/projects/{ProjA}/info");

		var select = _page.GetByTestId("nav-project-select");
		await Expect(select.Locator($"option[value=\"{ProjB}\"]")).ToHaveCountAsync(1);
		// The hidden marker that drives the zone-preserving redirect must say "admin".
		await Expect(_page.GetByTestId("nav-project-zone")).ToHaveAttributeAsync("value", "admin");

		// Choose the OTHER project via the selector — onchange submits the switch form.
		await select.SelectOptionAsync(new SelectOptionValue { Value = ProjB });

		// Zone preserved: still under /ui/admin/... for the CHOSEN project, NOT the /ui zone.
		await _page.WaitForURLAsync($"**/ui/admin/ws/{Ws}/projects/{ProjB}/**");
		_page.Url.Should().Contain($"/ui/admin/ws/{Ws}/projects/{ProjB}");
		_page.Url.Should().NotContain($"/ui/{Ws}/{ProjB}");
	}

	// Mirrors KpVotesOnboardingTests.EnsureProject: idempotent create via the admin UI.
	async Task EnsureProject(string key, string name)
	{
		await TestWorkspace.EnsureAsync(_page!);
		await _page!.GotoAsync($"/ui/{Ws}");
		var existing = _page.GetByTestId("nav-project-select").Locator($"option[value=\"{key}\"]");
		if (await existing.CountAsync() > 0) return;

		await _page.GotoAsync($"/ui/admin/ws/{Ws}/projects");
		await _page.GetByTestId("admin-project-create-key").FillAsync(key);
		await _page.GetByTestId("admin-project-create-name").FillAsync(name);
		await _page.GetByTestId("admin-project-create-desc").FillAsync("E2E nav selector fixture");
		await _page.GetByTestId("admin-project-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
	}
}
