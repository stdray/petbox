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
	public async Task New_Binding_Link_Opens_Editor()
	{
		await _page!.GotoAsync("/ui/config");
		await _page.GetByTestId("config-new").ClickAsync();
		await _page.WaitForURLAsync("**/ui/config/edit");
		await Expect(_page.GetByTestId("config-edit-form")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task Binding_Create_And_Appears_In_Table()
	{
		await _page!.GotoAsync("/ui/config/edit");

		await _page.GetByTestId("config-edit-path").FillAsync("db.host");
		await _page.GetByTestId("config-edit-value").FillAsync("localhost");
		await _page.GetByTestId("config-edit-tags").FillAsync("env=prod");
		await _page.GetByTestId("config-save-btn").ClickAsync();

		await _page.WaitForURLAsync("**/ui/config");
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("db.host");
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("localhost");
	}

	[Fact]
	public async Task Binding_Edit_And_Save()
	{
		// First create a binding
		await _page!.GotoAsync("/ui/config/edit");
		await _page.GetByTestId("config-edit-path").FillAsync("cache.ttl");
		await _page.GetByTestId("config-edit-value").FillAsync("300");
		await _page.GetByTestId("config-edit-tags").FillAsync("env=prod");
		await _page.GetByTestId("config-save-btn").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("cache.ttl");

		// Now edit it
		var editLink = _page.GetByTestId("config-row").Filter(new() { HasText = "cache.ttl" })
			.GetByTestId("config-edit-btn");
		await editLink.ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		output.WriteLine($"URL after edit click: {_page.Url}");

		await _page.GetByTestId("config-edit-value").FillAsync("600");
		await _page.GetByTestId("config-save-btn").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		output.WriteLine($"URL after save: {_page.Url}");

		if (_page.Url.Contains("handler=Save"))
		{
			var content = await _page.ContentAsync();
			output.WriteLine($"PAGE CONTENT (first 1000): {content[..Math.Min(1000, content.Length)]}");
		}

		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("600");
	}

	[Fact]
	public async Task Binding_Edit_Cancel_Returns_To_Index()
	{
		await _page!.GotoAsync("/ui/config/edit");
		await _page.GetByTestId("config-edit-path").FillAsync("cancel.test");
		await _page.GetByTestId("config-edit-value").FillAsync("original");
		await _page.GetByTestId("config-edit-tags").FillAsync("env=dev");
		await _page.GetByTestId("config-save-btn").ClickAsync();
		await _page.WaitForURLAsync("**/ui/config");

		var editLink = _page.GetByTestId("config-row").Filter(new() { HasText = "cancel.test" })
			.GetByTestId("config-edit-btn");
		await editLink.ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// Click Cancel — should return to index without saving
		await _page.GetByTestId("config-cancel-btn").ClickAsync();
		await _page.WaitForURLAsync("**/ui/config");

		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("original");
	}

	[Fact]
	public async Task Binding_Delete_Removes_From_Table()
	{
		await _page!.GotoAsync("/ui/config/edit");
		await _page.GetByTestId("config-edit-path").FillAsync("to.delete");
		await _page.GetByTestId("config-edit-value").FillAsync("x");
		await _page.GetByTestId("config-edit-tags").FillAsync("env=test");
		await _page.GetByTestId("config-save-btn").ClickAsync();
		await _page.WaitForURLAsync("**/ui/config");
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("to.delete");

		// Delete: confirm dialog
		_page.Dialog += (_, dialog) => dialog.AcceptAsync();

		var deleteBtn = _page.GetByTestId("config-row").Filter(new() { HasText = "to.delete" })
			.GetByTestId("config-delete");
		await deleteBtn.ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("config-table")).Not.ToContainTextAsync("to.delete");
	}
}
