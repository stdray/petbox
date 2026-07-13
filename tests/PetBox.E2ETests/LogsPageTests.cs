using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class LogsPageTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
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
	public async Task LogsPage_Renders_KqlInput()
	{
		await _page!.GotoAsync("/ui/$system/$system/logs");

		await Expect(_page.GetByTestId("kql-input")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("kql-apply")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task LogsPage_Shows_Empty_State()
	{
		await _page!.GotoAsync("/ui/$system/$system/logs");

		await Expect(_page.GetByTestId("events-empty")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task LogsPage_Apply_Shows_Empty_Result()
	{
		await _page!.GotoAsync("/ui/$system/$system/logs");

		await _page.GetByTestId("kql-input").FillAsync("events | where Level >= 3");
		await _page.GetByTestId("kql-apply").ClickAsync();

		await Expect(_page.GetByTestId("events-empty")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task LogsPage_Pin_Toggle_Works()
	{
		await _page!.GotoAsync("/ui/$system/$system/logs");

		var toggle = _page.GetByTestId("kql-pin-toggle");
		await Expect(toggle).ToBeVisibleAsync();

		await toggle.ClickAsync();
		await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "true");
	}

	// THE test for the FOUC fix (work kql-panel-pin-server-state), mirroring
	// SidebarPinTests.Server_Renders_Correct_DrawerClass_In_The_First_Response. The toggle test
	// above could pass even in the old, broken world (post-hydration JS forced the DOM to agree
	// with localStorage) — that is exactly what made the flicker invisible to a DOM-only
	// assertion. This test inspects the raw HTTP response body instead.
	[Fact]
	public async Task Server_Renders_Correct_PinClasses_In_The_First_Response()
	{
		// No cookie yet (first visit): server default is unpinned.
		var unpinnedResponse = await _page!.GotoAsync("/ui/$system/$system/logs");
		var unpinnedHtml = await unpinnedResponse!.TextAsync();
		unpinnedHtml.Should().NotContain("sticky top-0 z-20 shadow-lg",
			"a first-time visitor has no petbox.ui cookie yet, so the server must fall back to the unpinned default");

		// Set the cookie the framework itself reads/writes — no browser JS ever ran to produce
		// this value, it is set directly on the context, the same way a returning visitor's
		// browser would already be carrying it into the request.
		await _ctx!.AddCookiesAsync(
		[
			new Cookie
			{
				Name = "petbox.ui",
				Value = Uri.EscapeDataString("""{"kqlPanelPinned":true}"""),
				Url = app.BaseUrl,
			}
		]);

		var pinnedResponse = await _page.GotoAsync("/ui/$system/$system/logs");
		var pinnedHtml = await pinnedResponse!.TextAsync();
		pinnedHtml.Should().Contain("sticky top-0 z-20 shadow-lg",
			"the server must honor the cookie's kqlPanelPinned:true on the very first response, not print the unpinned form and let a script correct it after paint");
		pinnedHtml.Should().Contain("aria-pressed=\"true\"");
	}

	// live-tail-sse-transport-broken, the half only a REAL browser can prove: site.ts imported htmx's
	// core and nothing else, so hx-ext="sse" / sse-connect were inert attributes — the toggle flipped,
	// the container was inserted, and no EventSource was ever opened. Delivery semantics are pinned in
	// the integration suite (LogLiveTailTests), where they are deterministic; what is asserted HERE is
	// the thing that was actually broken and that only a browser executes: the bundled extension
	// registers, htmx processes the runtime-inserted container, and an EventSource connection is really
	// made — and answered (200 text/event-stream), which also proves the cookie session authenticates
	// against the endpoint that used to 401 every browser.
	[Fact]
	public async Task LiveTail_Toggle_Opens_An_Authenticated_EventSource()
	{
		await _page!.GotoAsync("/ui/$system/$system/logs");

		var stream = _page.WaitForResponseAsync(
			r => r.Url.Contains("/live-tail", StringComparison.Ordinal),
			new PageWaitForResponseOptions { Timeout = 15_000 });

		// Deliberately on an EMPTY table: the empty state used to replace the whole table, so
		// #events-body — the tail's swap target — did not exist and the toggle was a dead switch
		// exactly where a user is most likely to sit waiting for the next event.
		await _page.GetByTestId("live-tail-toggle").CheckAsync();

		var response = await stream;
		response.Status.Should().Be(200,
			"an EventSource cannot send X-Api-Key — the browser's cookie session must be accepted here");
		(await response.HeaderValueAsync("content-type"))
			.Should().Contain("text/event-stream", "the stream must actually be an SSE stream");
	}

	[Fact]
	public async Task LogsPage_KqlError_On_Bad_Syntax()
	{
		await _page!.GotoAsync("/ui/$system/$system/logs");

		await _page.GetByTestId("kql-input").FillAsync("garbage syntax !!!");
		await _page.GetByTestId("kql-apply").ClickAsync();

		await Expect(_page.GetByTestId("kql-error")).ToBeVisibleAsync();
	}
}
