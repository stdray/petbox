using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

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
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");
		await Expect(_page.GetByTestId("project-service-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Service_Create_And_Appears_In_Table()
	{
		var svcKey = "svc-" + Guid.NewGuid().ToString("N")[..6];
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");

		await _page.GetByTestId("project-service-create-key").FillAsync(svcKey);
		await _page.GetByTestId("project-service-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-services-table")).ToContainTextAsync(svcKey);
	}

	[Fact]
	public async Task Service_Delete_Removes_From_Table()
	{
		var svcKey = "svc-" + Guid.NewGuid().ToString("N")[..6];
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");

		// Create
		await _page.GetByTestId("project-service-create-key").FillAsync(svcKey);
		await _page.GetByTestId("project-service-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-services-table")).ToContainTextAsync(svcKey);

		// Delete the row we just created — target by row text to be robust against
		// other services that may have been created by sibling tests in the shared DB.
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		var row = _page.GetByTestId("service-row").Filter(new() { HasText = svcKey });
		await row.GetByTestId("project-service-delete").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-services-table")).Not.ToContainTextAsync(svcKey);
	}

	[Fact]
	public async Task Key_Create_Form_Is_Visible()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");
		await Expect(_page.GetByTestId("project-key-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Key_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");

		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-key-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-scope-logs:ingest").CheckAsync();
		await _page.GetByTestId("project-key-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-keys-table")).ToContainTextAsync("yb_key_");
	}

	[Fact]
	public async Task Key_Revoke_Removes_From_Table()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");

		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-key-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-scope-logs:ingest").CheckAsync();
		await _page.GetByTestId("project-key-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// The success banner shows the full just-issued key value; use its prefix
		// to target the row in the table (sibling tests share the DB and may add
		// rows with the same scope set, so filtering by scope text isn't unique).
		var newKey = await _page.GetByTestId("project-key-created").Locator("code").InnerTextAsync();
		var keyPrefix = newKey[..12];

		var row = _page.GetByTestId("project-keys-table")
			.Locator("tr").Filter(new() { HasText = keyPrefix });
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		await row.GetByTestId("project-key-revoke").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		await Expect(_page.GetByTestId("project-keys-table")).Not.ToContainTextAsync(keyPrefix);
	}
}
