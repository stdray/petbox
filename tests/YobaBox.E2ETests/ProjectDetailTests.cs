using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class ProjectDetailTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task Service_Create_Form_Appears_On_Click()
	{
		await _page!.GotoAsync("/admin/projects/$system");

		await _page.GetByTestId("project-service-new").ClickAsync();
		await Expect(_page.GetByTestId("project-service-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Service_Create_Cancel_Clears_Form()
	{
		await _page!.GotoAsync("/admin/projects/$system");

		await _page.GetByTestId("project-service-new").ClickAsync();
		await Expect(_page.GetByTestId("project-service-create-form")).ToBeVisibleAsync();

		await _page.GetByTestId("project-service-create-cancel").ClickAsync();
		await Expect(_page.GetByTestId("project-service-create-form")).ToHaveCountAsync(0);
	}

	[Fact]
	public async Task Service_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/admin/projects/$system");

		await _page.GetByTestId("project-service-new").ClickAsync();
		await Expect(_page.GetByTestId("project-service-create-form")).ToBeVisibleAsync();

		await _page.GetByTestId("project-service-create-key").FillAsync("test-svc");
		await _page.GetByTestId("project-service-create-submit").ClickAsync();

		// HX-Redirect reloads page
		await _page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

		await Expect(_page.GetByTestId("project-services-table")).ToContainTextAsync("test-svc");
	}

	[Fact]
	public async Task Key_Create_Form_Appears_On_Click()
	{
		await _page!.GotoAsync("/admin/projects/$system");

		await _page.GetByTestId("project-key-new").ClickAsync();
		await Expect(_page.GetByTestId("project-key-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Key_Create_Cancel_Clears_Form()
	{
		await _page!.GotoAsync("/admin/projects/$system");

		await _page.GetByTestId("project-key-new").ClickAsync();
		await Expect(_page.GetByTestId("project-key-create-form")).ToBeVisibleAsync();

		await _page.GetByTestId("project-key-create-cancel").ClickAsync();
		await Expect(_page.GetByTestId("project-key-create-form")).ToHaveCountAsync(0);
	}

	[Fact]
	public async Task Key_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/admin/projects/$system");

		await _page.GetByTestId("project-key-new").ClickAsync();
		await Expect(_page.GetByTestId("project-key-create-form")).ToBeVisibleAsync();

		await _page.GetByTestId("project-key-create-scopes").FillAsync("logs:ingest");
		await _page.GetByTestId("project-key-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

		await Expect(_page.GetByTestId("project-keys-table")).ToContainTextAsync("yb_key_");
	}
}
