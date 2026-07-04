using System.Text.RegularExpressions;

namespace PetBox.E2ETests;

// The sidebar pin toggle must behave identically in every zone. Regression guard
// for work/sidebar-pin-missing: the button existed only in /ui and was absent from
// /ui/admin and /ui/me. It now lives in a shared partial (_SidebarPin) and the
// controlling JS (ts/sidebar.ts) selects the drawer by class, not a zone-specific id.
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

		// Choice is remembered (localStorage) — a reload comes back unpinned.
		await _page.ReloadAsync();
		await Expect(_page.GetByTestId("nav-sidebar-pin")).ToHaveAttributeAsync("aria-pressed", "false");
		await Expect(_page.Locator(".drawer")).Not.ToHaveClassAsync(new Regex(@"\bdrawer-open\b"));
	}
}
