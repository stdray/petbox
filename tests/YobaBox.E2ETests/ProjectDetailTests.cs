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
	public async Task Service_Create_Form_Is_Visible()
	{
		await _page!.GotoAsync("/ui/ui/admin/projects/$system");
		await Expect(_page.GetByTestId("project-service-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Service_Create_And_Appears_In_Table()
	{
		var svcKey = "svc-" + Guid.NewGuid().ToString("N")[..6];
		await _page!.GotoAsync("/ui/ui/admin/projects/$system");

		await _page.GetByTestId("project-service-create-key").FillAsync(svcKey);
		await _page.GetByTestId("project-service-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-services-table")).ToContainTextAsync(svcKey);
	}

	[Fact]
	public async Task Service_Delete_Removes_From_Table()
	{
		var svcKey = "svc-" + Guid.NewGuid().ToString("N")[..6];
		await _page!.GotoAsync("/ui/ui/admin/projects/$system");

		// Create
		await _page.GetByTestId("project-service-create-key").FillAsync(svcKey);
		await _page.GetByTestId("project-service-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-services-table")).ToContainTextAsync(svcKey);

		// Delete
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		await _page.GetByTestId("project-service-delete").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-services-table")).Not.ToContainTextAsync(svcKey);
	}

	[Fact]
	public async Task Key_Create_Form_Is_Visible()
	{
		await _page!.GotoAsync("/ui/ui/admin/projects/$system");
		await Expect(_page.GetByTestId("project-key-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Key_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/ui/ui/admin/projects/$system");

		await _page.GetByTestId("project-key-create-scopes").FillAsync("logs:ingest");
		await _page.GetByTestId("project-key-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-keys-table")).ToContainTextAsync("yb_key_");
	}

	[Fact]
	public async Task Key_Revoke_Removes_From_Table()
	{
		await _page!.GotoAsync("/ui/ui/admin/projects/$system");

		// Create
		await _page.GetByTestId("project-key-create-scopes").FillAsync("dashboard:read");
		await _page.GetByTestId("project-key-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// Revoke the key we just created (find row with our scopes)
		var row = _page.GetByTestId("project-keys-table")
			.Locator("tr").Filter(new() { HasText = "dashboard:read" });
		var revokeBtn = row.GetByTestId("project-key-revoke");
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		await revokeBtn.ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// The key should be gone (only system internal key remains)
		await Expect(_page.GetByTestId("project-keys-table")).Not.ToContainTextAsync("dashboard:read");
	}
}
