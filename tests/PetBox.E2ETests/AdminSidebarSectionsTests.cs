using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// Regression guard for work `admin-sidebar-sections`: the admin sidebar's three named
// sections (Server/Workspace/Project administration) must each remember their own collapsed
// state through the shared petbox.ui cookie (BrowserState.AdminSectionsCollapsed), resolved
// server-side BEFORE render — same discipline SidebarPinTests already proves for the pin.
// Server_Renders_Correct_OpenState_In_The_First_Response below inspects the RAW response
// body, not the post-hydration DOM, for the same reason SidebarPinTests' own first-response
// test does: a DOM-only assertion cannot distinguish "the server got it right" from "a script
// silently corrected it after paint".
[Collection(nameof(UiCollection))]
public sealed class AdminSidebarSectionsTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string AdminUrl = "/ui/admin/ws/$system/projects";

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
	public async Task AllThreeSections_Present_And_Expanded_By_Default()
	{
		await _page!.GotoAsync(AdminUrl);

		foreach (var section in new[] { "admin-side-server-section", "admin-side-workspace-section", "admin-side-project-section" })
		{
			var details = _page.GetByTestId(section);
			await Expect(details).ToBeVisibleAsync();
			// The daisyUI/HTML native <details> element's own open state — not a CSS class — so
			// this is the true collapsed/expanded signal, checked the same way the raw-response
			// test below checks it server-side.
			(await details.EvaluateAsync<bool>("el => el.open")).Should().BeTrue(
				$"{section} must be expanded by default (no petbox.ui cookie yet)");
		}
	}

	[Fact]
	public async Task Collapsing_OneSection_Persists_Across_Reload_WithoutAffectingOthers()
	{
		await _page!.GotoAsync(AdminUrl);

		var workspaceSection = _page.GetByTestId("admin-side-workspace-section");
		await workspaceSection.Locator("summary").ClickAsync();
		(await workspaceSection.EvaluateAsync<bool>("el => el.open")).Should().BeFalse(
			"clicking the summary toggles the native <details> closed");

		await _page.ReloadAsync();

		(await _page.GetByTestId("admin-side-workspace-section").EvaluateAsync<bool>("el => el.open")).Should().BeFalse(
			"the collapsed state must be remembered across reload (petbox.ui cookie, written by ts/sidebar.ts's admin-section toggle listener)");
		// Sibling sections are untouched — proves the cookie merges per-section rather than
		// clobbering the whole AdminSectionsCollapsed map on one toggle.
		(await _page.GetByTestId("admin-side-server-section").EvaluateAsync<bool>("el => el.open")).Should().BeTrue();
		(await _page.GetByTestId("admin-side-project-section").EvaluateAsync<bool>("el => el.open")).Should().BeTrue();
	}

	// THE test for the no-flicker guarantee. The two tests above could pass even if the server
	// always rendered every section open and a script corrected it after paint — post-hydration
	// DOM state looks identical either way. This test inspects the raw HTTP response body
	// instead, before Chromium has parsed a single byte of it (mirrors SidebarPinTests.
	// Server_Renders_Correct_DrawerClass_In_The_First_Response exactly).
	[Fact]
	public async Task Server_Renders_Correct_OpenState_In_The_First_Response()
	{
		// No cookie yet: server default is expanded — the rendered <details> carries the native
		// `open="open"` attribute Razor's boolean-attribute serialization produces.
		var defaultResponse = await _page!.GotoAsync(AdminUrl);
		var defaultHtml = await defaultResponse!.TextAsync();
		defaultHtml.Should().Contain("<details open=\"open\" data-testid=\"admin-side-workspace-section\"",
			"a first-time visitor has no petbox.ui cookie yet, so the server must fall back to expanded");

		// Set the cookie the framework itself reads/writes — no browser JS ever ran to produce
		// this value, exactly the same technique SidebarPinTests uses for the pin.
		await _ctx!.AddCookiesAsync(
		[
			new Cookie
			{
				Name = "petbox.ui",
				Value = Uri.EscapeDataString("""{"adminSectionsCollapsed":{"workspace":true}}"""),
				Url = app.BaseUrl,
			}
		]);

		var collapsedResponse = await _page.GotoAsync(AdminUrl);
		var collapsedHtml = await collapsedResponse!.TextAsync();
		collapsedHtml.Should().NotContain("<details open=\"open\" data-testid=\"admin-side-workspace-section\"",
			"the server must honor the cookie's adminSectionsCollapsed.workspace:true on the very " +
			"first response, not render it open and let a script collapse it after paint");
		collapsedHtml.Should().Contain("<details data-testid=\"admin-side-workspace-section\"",
			"the collapsed section still renders — just without the open attribute");
		// The OTHER two sections are unaffected by that one cookie key.
		collapsedHtml.Should().Contain("<details open=\"open\" data-testid=\"admin-side-server-section\"");
		collapsedHtml.Should().Contain("<details open=\"open\" data-testid=\"admin-side-project-section\"");
	}
}
