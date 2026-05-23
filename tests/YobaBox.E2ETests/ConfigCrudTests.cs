using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class ConfigCrudTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task Binding_Create_Inline_Form_Appears()
	{
		await _page!.GotoAsync("/config");

		await _page.GetByTestId("config-new").ClickAsync();
		await Expect(_page.Locator("#config-new-row input[name='Path']")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Binding_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/config");

		await _page.GetByTestId("config-new").ClickAsync();
		await Expect(_page.Locator("#config-new-row input[name='Path']")).ToBeVisibleAsync();

		var row = _page.Locator("#config-new-row");
		await row.Locator("input[name='Path']").FillAsync("db.host");
		await row.Locator("input[name='Value']").FillAsync("localhost");
		await row.Locator("textarea[name='Tags']").FillAsync("env=prod");

		await _page.GetByTestId("config-save-btn").ClickAsync();

		// After save, _Row partial appears — check the table has our value
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("db.host");
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("localhost");
	}

	[Fact]
	public async Task Binding_Edit_Inline_And_Save()
	{
		// First create a binding
		await _page!.GotoAsync("/config");
		await _page.GetByTestId("config-new").ClickAsync();
		var row = _page.Locator("#config-new-row");
		await row.Locator("input[name='Path']").FillAsync("cache.ttl");
		await row.Locator("input[name='Value']").FillAsync("300");
		await row.Locator("textarea[name='Tags']").FillAsync("env=prod");
		await _page.GetByTestId("config-save-btn").ClickAsync();
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("cache.ttl");

		// Now edit it
		var editBtn = _page.GetByTestId("config-row").Filter(new() { HasText = "cache.ttl" })
			.GetByTestId("config-edit-btn");
		await editBtn.ClickAsync();
		await Expect(_page.GetByTestId("config-edit-row")).ToBeVisibleAsync();

		// Change value
		var editRow = _page.GetByTestId("config-edit-row");
		await editRow.Locator("input[name='Value']").FillAsync("600");
		await _page.GetByTestId("config-save-btn").ClickAsync();

		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("600");
	}

	[Fact]
	public async Task Binding_Edit_Cancel_Restores_Row()
	{
		await _page!.GotoAsync("/config");
		await _page.GetByTestId("config-new").ClickAsync();
		var row = _page.Locator("#config-new-row");
		await row.Locator("input[name='Path']").FillAsync("cancel.test");
		await row.Locator("input[name='Value']").FillAsync("original");
		await row.Locator("textarea[name='Tags']").FillAsync("env=dev");
		await _page.GetByTestId("config-save-btn").ClickAsync();
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("cancel.test");

		// Edit then cancel
		var editBtn = _page.GetByTestId("config-row").Filter(new() { HasText = "cancel.test" })
			.GetByTestId("config-edit-btn");
		await editBtn.ClickAsync();
		await Expect(_page.GetByTestId("config-edit-row")).ToBeVisibleAsync();

		var editRow = _page.GetByTestId("config-edit-row");
		await editRow.Locator("input[name='Value']").FillAsync("changed");
		await _page.GetByTestId("config-cancel-btn").ClickAsync();

		// Original value should still be there
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("original");
	}

	[Fact]
	public async Task Binding_Delete_Removes_From_Table()
	{
		await _page!.GotoAsync("/config");
		await _page.GetByTestId("config-new").ClickAsync();
		var row = _page.Locator("#config-new-row");
		await row.Locator("input[name='Path']").FillAsync("to.delete");
		await row.Locator("input[name='Value']").FillAsync("x");
		await row.Locator("textarea[name='Tags']").FillAsync("env=test");
		await _page.GetByTestId("config-save-btn").ClickAsync();
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("to.delete");

		// Handle the confirm dialog
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();

		var deleteBtn = _page.GetByTestId("config-row").Filter(new() { HasText = "to.delete" })
			.GetByTestId("config-delete");
		await deleteBtn.ClickAsync();

		// After delete, page reloads with deleteSuccess flag
		await _page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("config-table")).Not.ToContainTextAsync("to.delete");
		await Expect(_page.GetByTestId("config-success")).ToContainTextAsync("Binding deleted");
	}
}
