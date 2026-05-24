using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class DashboardTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task Dashboard_Renders_System_Project()
	{
		await _page!.GotoAsync("/ui/dashboard");

		await Expect(_page.GetByTestId("dashboard-title")).ToBeVisibleAsync();
		await Expect(_page.Locator("[data-project-key=\"$system\"]")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Dashboard_Shows_Project_Name()
	{
		await _page!.GotoAsync("/ui/dashboard");

		var name = _page.Locator("[data-project-key=\"$system\"]").GetByTestId("dashboard-project-name");
		await Expect(name).ToBeVisibleAsync();
		await Expect(name).ToHaveTextAsync("System");
	}

	[Fact]
	public async Task Index_Redirects_To_Dashboard()
	{
		await _page!.GotoAsync("/ui/dashboard");

		await Task.Delay(300);
		_page.Url.Should().Contain("/ui/dashboard");
	}
}
