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
		await _page!.GotoAsync("/ui/$system/$system");

		await Expect(_page.GetByTestId("kql-input")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("kql-apply")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task LogsPage_Shows_Empty_State()
	{
		await _page!.GotoAsync("/ui/$system/$system");

		await Expect(_page.GetByTestId("events-empty")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task LogsPage_Apply_Shows_Empty_Result()
	{
		await _page!.GotoAsync("/ui/$system/$system");

		await _page.GetByTestId("kql-input").FillAsync("events | where Level >= 3");
		await _page.GetByTestId("kql-apply").ClickAsync();

		await Expect(_page.GetByTestId("events-empty")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task LogsPage_Pin_Toggle_Works()
	{
		await _page!.GotoAsync("/ui/$system/$system");

		var toggle = _page.GetByTestId("kql-pin-toggle");
		await Expect(toggle).ToBeVisibleAsync();

		await toggle.ClickAsync();
		await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "true");
	}

	[Fact]
	public async Task LogsPage_KqlError_On_Bad_Syntax()
	{
		await _page!.GotoAsync("/ui/$system/$system");

		await _page.GetByTestId("kql-input").FillAsync("garbage syntax !!!");
		await _page.GetByTestId("kql-apply").ClickAsync();

		await Expect(_page.GetByTestId("kql-error")).ToBeVisibleAsync();
	}
}
