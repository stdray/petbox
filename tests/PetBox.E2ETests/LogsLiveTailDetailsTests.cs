using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// live-tail-row-details-unexpandable: LogApi.RenderEvent streams a bare <tr class="event-live"> with
// no paired <tr class="event-details"> sibling (unlike _EventRow.cshtml for a normal, non-live row),
// so a live row's expand click was always a no-op — and stayed that way even after live tail was
// switched off, since the streamed row is never replaced. EventDetailsApi (Pages/Logs/
// EventDetails.cshtml.cs) fixed it: ts/logs.ts now fetches the details fragment lazily on a live row's
// first click. LogEventDetailsApiTests pins the server side (auth boundaries, content) deterministically
// — this suite is deliberately the thin layer only a REAL browser can prove: the bundled htmx SSE
// extension delivers a live row, the click handler's fetch actually round-trips, and the DOM actually
// gets the missing sibling and shows it.
[Collection(nameof(UiCollection))]
public sealed class LogsLiveTailDetailsTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;

	const string Project = "livetaildetails";

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

	// Mirrors ApiKeyScopeTests.EnsureProject — its own project, so this suite does not depend on
	// another test class in the collection having created one first.
	async Task EnsureProjectAsync()
	{
		await TestWorkspace.EnsureAsync(_page!);
		await _page!.GotoAsync($"/ui/{TestWorkspace.Key}");
		var exists = await _page.GetByTestId("nav-project-select").Locator($"option[value=\"{Project}\"]").CountAsync();
		if (exists > 0) return;

		await _page.GotoAsync($"/ui/admin/ws/{TestWorkspace.Key}/projects");
		await _page.GetByTestId("admin-project-create-key").FillAsync(Project);
		await _page.GetByTestId("admin-project-create-name").FillAsync("Live Tail Details");
		await _page.GetByTestId("admin-project-create-desc").FillAsync("E2E fixture project");
		await _page.GetByTestId("admin-project-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
	}

	async Task<string> CreateKeyAsync()
	{
		await _page!.GotoAsync($"/ui/admin/ws/{TestWorkspace.Key}/projects/{Project}/keys");
		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-{Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-create-scopes-group").ScrollIntoViewIfNeededAsync();
		foreach (var scope in new[] { "logs:admin", "logs:ingest" })
			await _page.GetByTestId($"project-key-scope-{scope}").CheckAsync();
		await _page.GetByTestId("project-key-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var keyEl = _page.GetByTestId("project-key-created").Locator("code");
		await Expect(keyEl).ToBeVisibleAsync();
		return (await keyEl.TextContentAsync())!.Trim();
	}

	async Task EnsureLogAsync(string key)
	{
		var resp = await _page!.APIRequest.PostAsync($"/api/logs/{Project}/logs", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = key },
			DataObject = new { name = "default" },
		});
		resp.Status.Should().BeOneOf(201, 409);
	}

	async Task IngestAsync(string key, string message, string widgetValue)
	{
		var payload =
			$$"""{"@t":"{{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}}","@l":"Information","@m":"{{message}}","Widget":"{{widgetValue}}"}""";
		var resp = await _page!.APIRequest.PostAsync($"/api/ingest/{Project}/default/clef", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = key, ["X-Service-Key"] = "e2e" },
			Data = payload,
		});
		resp.Status.Should().Be(200);
	}

	[Fact]
	public async Task LiveTail_Row_Can_Be_Expanded_And_Shows_Properties()
	{
		await EnsureProjectAsync();
		var key = await CreateKeyAsync();
		await EnsureLogAsync(key);

		await _page!.GotoAsync($"/ui/{TestWorkspace.Key}/{Project}/logs/default");

		var stream = _page.WaitForResponseAsync(
			r => r.Url.Contains("/live-tail", StringComparison.Ordinal),
			new PageWaitForResponseOptions { Timeout = 15_000 });
		await _page.GetByTestId("live-tail-toggle").CheckAsync();
		var streamResponse = await stream;
		streamResponse.Status.Should().Be(200, "the tail must actually open before an event can ride it in");

		var marker = $"live-details-{Guid.NewGuid():N}";
		var widgetValue = $"gizmo-{Guid.NewGuid():N}";
		await IngestAsync(key, marker, widgetValue);

		var row = _page.GetByTestId("events-row").Filter(new() { HasText = marker });
		await Expect(row).ToBeVisibleAsync(new() { Timeout = 15_000 });
		// Must be the SSE-streamed row the fix targets, not one a page reload would already carry
		// details for.
		await Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("event-live"));

		// Nothing to expand yet — the whole point: LogApi.RenderEvent never sent a details sibling.
		await Expect(_page.GetByTestId("event-details").Filter(new() { HasText = widgetValue })).ToHaveCountAsync(0);

		var detailsFetch = _page.WaitForResponseAsync(
			r => r.Request.Method == "GET" && r.Url.Contains($"/logs/{Project}/default/events/", StringComparison.Ordinal),
			new PageWaitForResponseOptions { Timeout = 15_000 });
		await row.ClickAsync();
		var detailsResponse = await detailsFetch;
		detailsResponse.Status.Should().Be(200, "the click must round-trip to EventDetailsApi, not silently do nothing");

		var details = _page.GetByTestId("event-details").Filter(new() { HasText = widgetValue });
		await Expect(details).ToBeVisibleAsync(
			new() { Timeout = 5_000 }); // ToBeVisible fails on a "hidden" (display:none) match too
		await Expect(details).ToContainTextAsync(widgetValue);

		// A second click toggles the ALREADY-inserted row — no second fetch is exercised here (that
		// would be a race to assert reliably in a browser test), but the DOM behavior IS: it collapses
		// instead of erroring or duplicating.
		await row.ClickAsync();
		await Expect(details).ToBeHiddenAsync();
	}

	// The other half of live-tail-row-details-unexpandable, and the owner's actual complaint ("даже
	// после выключения нельзя"): switching live tail OFF never replaced the streamed rows already in
	// the DOM, so a row delivered while tailing stayed permanently unexpandable until a full reload.
	// fetchAndInsertDetails (ts/logs.ts) does not gate on the toggle's checked state — it only reads
	// the toggle's data-project/data-log attributes, which the toggle carries regardless of checked —
	// so the fix is that a live row remains expandable after the toggle flips off. This test proves
	// that specifically: ingest while tailing, flip the toggle OFF, THEN click.
	[Fact]
	public async Task LiveTail_Row_Still_Expandable_After_Tail_Switched_Off()
	{
		await EnsureProjectAsync();
		var key = await CreateKeyAsync();
		await EnsureLogAsync(key);

		await _page!.GotoAsync($"/ui/{TestWorkspace.Key}/{Project}/logs/default");

		var stream = _page.WaitForResponseAsync(
			r => r.Url.Contains("/live-tail", StringComparison.Ordinal),
			new PageWaitForResponseOptions { Timeout = 15_000 });
		var toggle = _page.GetByTestId("live-tail-toggle");
		await toggle.CheckAsync();
		var streamResponse = await stream;
		streamResponse.Status.Should().Be(200, "the tail must actually open before an event can ride it in");

		var marker = $"live-off-{Guid.NewGuid():N}";
		var widgetValue = $"sprocket-{Guid.NewGuid():N}";
		await IngestAsync(key, marker, widgetValue);

		var row = _page.GetByTestId("events-row").Filter(new() { HasText = marker });
		await Expect(row).ToBeVisibleAsync(new() { Timeout = 15_000 });
		await Expect(row).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("event-live"));

		// Switch live tail OFF before ever clicking the row — the exact sequence that used to leave it
		// dead: the SSE container is torn down, but the row itself (and its missing details sibling)
		// stays in the DOM untouched.
		await toggle.UncheckAsync();
		await Expect(toggle).Not.ToBeCheckedAsync();

		var detailsFetch = _page.WaitForResponseAsync(
			r => r.Request.Method == "GET" && r.Url.Contains($"/logs/{Project}/default/events/", StringComparison.Ordinal),
			new PageWaitForResponseOptions { Timeout = 15_000 });
		await row.ClickAsync();
		var detailsResponse = await detailsFetch;
		detailsResponse.Status.Should().Be(200, "the row must still be expandable after live tail is switched off");

		var details = _page.GetByTestId("event-details").Filter(new() { HasText = widgetValue });
		await Expect(details).ToBeVisibleAsync(new() { Timeout = 5_000 });
		await Expect(details).ToContainTextAsync(widgetValue);
	}
}
