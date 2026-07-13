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
	public async Task HealthEndpoint_Create_Form_Is_Visible()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");
		await Expect(_page.GetByTestId("project-health-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task HealthEndpoint_Create_And_Appears_In_Table()
	{
		var url = $"https://svc-{Guid.NewGuid().ToString("N")[..6]}.example/health";
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");

		await _page.GetByTestId("project-health-create-url").FillAsync(url);
		await _page.GetByTestId("project-health-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-health-table")).ToContainTextAsync(url);
	}

	[Fact]
	public async Task HealthEndpoint_Delete_Removes_From_Table()
	{
		var url = $"https://svc-{Guid.NewGuid().ToString("N")[..6]}.example/health";
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/info");

		await _page.GetByTestId("project-health-create-url").FillAsync(url);
		await _page.GetByTestId("project-health-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-health-table")).ToContainTextAsync(url);

		// Delete the row we just created — target by row text to be robust against
		// rows created by sibling tests in the shared DB.
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();
		var row = _page.GetByTestId("health-endpoint-row").Filter(new() { HasText = url });
		await row.GetByTestId("project-health-delete").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-health-table")).Not.ToContainTextAsync(url);
	}

	[Fact]
	public async Task Key_Create_Form_Is_Visible()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/keys");
		await Expect(_page.GetByTestId("project-key-create-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Key_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/keys");

		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-key-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-scope-logs:ingest").CheckAsync();
		await _page.GetByTestId("project-key-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("project-keys-table")).ToContainTextAsync("yb_key_");
	}

	[Fact]
	public async Task Key_Revoke_Removes_From_Table()
	{
		await _page!.GotoAsync("/ui/admin/ws/$system/projects/$system/keys");

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
