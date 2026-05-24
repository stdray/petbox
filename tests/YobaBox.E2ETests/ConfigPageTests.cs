using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class ConfigPageTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task ConfigPage_Renders()
	{
		await _page!.GotoAsync("/config");

		await Expect(_page.GetByTestId("config-title")).ToBeVisibleAsync();
		await Expect(_page.Locator("body")).ToContainTextAsync("Bindings");
	}

	[Fact]
	public async Task ConfigPage_New_Button_Visible()
	{
		await _page!.GotoAsync("/config");

		await Expect(_page.GetByTestId("config-new")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task ConfigPage_Filter_Form_Renders()
	{
		await _page!.GotoAsync("/config");

		await Expect(_page.GetByTestId("config-filter-form")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-filter-key")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-filter-apply")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-filter-clear")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task ConfigPage_Clear_Filter_Restores()
	{
		await _page!.GotoAsync("/config");

		await _page.GetByTestId("config-filter-key").FillAsync("zzznonexistent");
		await _page.GetByTestId("config-filter-apply").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		await _page.GetByTestId("config-filter-clear").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.Locator("body")).ToContainTextAsync("Bindings");
	}
}
