using System.Text.RegularExpressions;
using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// The sidebar pin toggle must behave identically in every zone. Regression guard
// for work/sidebar-pin-missing: the button existed only in /ui and was absent from
// /ui/admin and /ui/me. It now lives in a shared partial (_SidebarPin) and the
// controlling JS (ts/sidebar.ts) selects the drawer by class, not a zone-specific id.
//
// work/sidebar-pin-server-state (2026-07-13): the pin used to live in localStorage while the
// server ALWAYS printed drawer-open — sidebar.ts stripped the class after load on every full
// page load, which is what made the sidebar visibly flicker on every board switch. The pin now
// resolves from the `petbox.ui` cookie (BrowserState.SidebarPinned) BEFORE render, so the FIRST
// response already carries the right class/aria state and sidebar.ts no longer corrects anything
// after load. Server_Renders_Correct_DrawerClass_In_The_First_Response below is the test that
// actually proves that (inspects the raw response body, not the post-hydration DOM).
[Collection(nameof(UiCollection))]
public sealed class SidebarPinTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	// One representative page per zone.
	static readonly (string Zone, string Url)[] Zones =
	[
		("ui", "/ui/$system"),
		("admin", "/ui/admin/sys/users"),
		("account", "/ui/me/account"),
	];

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
	public async Task Pin_Present_And_Pinned_By_Default_In_Every_Zone()
	{
		foreach (var (zone, url) in Zones)
		{
			await _page!.GotoAsync(url);
			var pin = _page.GetByTestId("nav-sidebar-pin");
			await Expect(pin).ToBeVisibleAsync();
			// Default state is pinned: the drawer is docked open, the button pressed.
			await Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");
			await Expect(_page.Locator(".drawer")).ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
		}
	}

	[Fact]
	public async Task Pin_Toggle_Unpins_And_Persists_Across_Reload()
	{
		await _page!.GotoAsync("/ui/admin/sys/users");
		var pin = _page.GetByTestId("nav-sidebar-pin");
		await Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");

		// Unpin: button un-presses and the drawer sheds its docked-open class.
		await pin.ClickAsync();
		await Expect(pin).ToHaveAttributeAsync("aria-pressed", "false");
		await Expect(_page.Locator(".drawer")).Not.ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));

		// Choice is remembered (the petbox.ui cookie, written by ts/sidebar.ts through
		// ui-state.ts's writeUiState) — a reload comes back unpinned. No manual cleanup needed
		// afterwards: InitializeAsync hands each test a fresh IBrowserContext (its own cookie jar),
		// so there is nothing here for a sibling test to leak into (unlike the old localStorage
		// value, which lived in the shared storageState snapshot).
		await _page.ReloadAsync();
		await Expect(_page.GetByTestId("nav-sidebar-pin")).ToHaveAttributeAsync("aria-pressed", "false");
		await Expect(_page.Locator(".drawer")).Not.ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
	}

	// board-ui-review-findings #4 (sidebar-unpin-admin-trap): root cause was `_AdminLayout.cshtml`
	// wrapping its hamburger toggle in `<div class="flex-none lg:hidden">` — invisible at this
	// test's desktop viewport (Playwright's default 1280x720, well past Tailwind's 1024px `lg`
	// breakpoint) — so once unpinned, the drawer (and the pin button living inside it,
	// _AdminSidebar → _ZoneSelectors → _SidebarPin) had NO way back open without navigating away
	// to /ui and re-pinning there. `_AccountLayout.cshtml` had the identical trap. Fixed by
	// dropping `lg:hidden`, matching `_Layout.cshtml`'s own toggle (never gated to mobile). This
	// test is the ACTUAL regression guard the brief asked for: unpin, then reopen + re-pin
	// WITHOUT ever leaving the admin zone — the old code could not do this at desktop width.
	[Fact]
	public async Task Unpin_In_Admin_Reopen_And_RePin_Without_Leaving_Admin()
	{
		const string adminUrl = "/ui/admin/sys/users";
		await _page!.GotoAsync(adminUrl);
		var pin = _page.GetByTestId("nav-sidebar-pin");
		var toggle = _page.GetByTestId("admin-nav-toggle");
		await Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");

		// Unpin: the drawer closes and the pin button (inside it) goes along with it.
		await pin.ClickAsync();
		await Expect(_page.Locator(".drawer")).Not.ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));

		// The hamburger toggle must be there AND actually visible at this (desktop) viewport —
		// `lg:hidden` would make Playwright's own visibility check fail here, which is exactly
		// the trap: the element exists in the DOM but a real user could never click it.
		await Expect(toggle).ToBeVisibleAsync();

		// Reopen as an overlay (the checkbox-driven drawer-side, independent of drawer-open).
		await toggle.ClickAsync();
		await Expect(pin).ToBeVisibleAsync();

		// Re-pin from inside that overlay — no navigation happened at any point.
		await pin.ClickAsync();
		await Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");
		await Expect(_page.Locator(".drawer")).ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
		_page.Url.Should().Contain(adminUrl, "the whole unpin/reopen/re-pin cycle must stay inside the admin zone — no trip to /ui required");
	}

	// Same trap, same fix, in the account zone (_AccountLayout.cshtml had the identical
	// `lg:hidden` wrapper) — the brief's own "check _AccountLayout too" note.
	[Fact]
	public async Task Unpin_In_Account_Reopen_And_RePin_Without_Leaving_Account()
	{
		const string accountUrl = "/ui/me/account";
		await _page!.GotoAsync(accountUrl);
		var pin = _page.GetByTestId("nav-sidebar-pin");
		var toggle = _page.GetByTestId("account-nav-toggle");
		await Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");

		await pin.ClickAsync();
		await Expect(_page.Locator(".drawer")).Not.ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
		await Expect(toggle).ToBeVisibleAsync();

		await toggle.ClickAsync();
		await Expect(pin).ToBeVisibleAsync();

		await pin.ClickAsync();
		await Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");
		await Expect(_page.Locator(".drawer")).ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
		_page.Url.Should().Contain(accountUrl, "the whole unpin/reopen/re-pin cycle must stay inside the account zone");
	}

	// THE test for the FOUC fix. Everything above could pass even in the old, broken world (post
	// hydration the JS forced the DOM to agree) — that is exactly what made the flicker invisible
	// to a DOM-only assertion. This test inspects the raw HTTP response body instead: whatever
	// text the server sent for THIS request, before Chromium has parsed or executed a single byte
	// of it. If the server ever regresses to always printing drawer-open and relying on a script
	// to correct it after paint, this test fails; a DOM-based assertion would not catch it.
	[Fact]
	public async Task Server_Renders_Correct_DrawerClass_In_The_First_Response()
	{
		// No cookie yet (first visit): server default is pinned.
		var pinnedResponse = await _page!.GotoAsync("/ui/admin/sys/users");
		var pinnedHtml = await pinnedResponse!.TextAsync();
		pinnedHtml.Should().Contain("class=\"drawer drawer-open\"",
			"a first-time visitor has no petbox.ui cookie yet, so the server must fall back to the pinned default");

		// Set the cookie the framework itself reads/writes — no browser JS ever ran to produce
		// this value, it is set directly on the context, the same way a returning visitor's
		// browser would already be carrying it into the request.
		await _ctx!.AddCookiesAsync(
		[
			new Cookie
			{
				Name = "petbox.ui",
				Value = Uri.EscapeDataString("""{"sidebarPinned":false}"""),
				Url = app.BaseUrl,
			}
		]);

		var unpinnedResponse = await _page.GotoAsync("/ui/admin/sys/users");
		var unpinnedHtml = await unpinnedResponse!.TextAsync();
		unpinnedHtml.Should().NotContain("class=\"drawer drawer-open\"",
			"the server must honor the cookie's sidebarPinned:false on the very first response, not print drawer-open and let a script strip it after paint");
		unpinnedHtml.Should().Contain("class=\"drawer \"",
			"the unpinned drawer still renders — just without the drawer-open token");
	}
}
