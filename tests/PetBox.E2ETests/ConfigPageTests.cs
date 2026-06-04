using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

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
		await _page!.GotoAsync("/ui/$system/config");

		await Expect(_page.GetByTestId("config-title")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-new")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task ConfigPage_New_Button_Visible()
	{
		await _page!.GotoAsync("/ui/$system/config");

		await Expect(_page.GetByTestId("config-new")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task ConfigPage_Filter_Form_Renders()
	{
		await _page!.GotoAsync("/ui/$system/config");

		await Expect(_page.GetByTestId("config-filter-form")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-filter-key")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-filter-apply")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-filter-clear")).ToBeVisibleAsync();
	}

	// Regression: the "+ New binding" button must actually NAVIGATE to the editor,
	// not just be visible. It was a dead link (asp-page link with no route values →
	// empty href) so clicking it did nothing.
	[Fact]
	public async Task ConfigPage_New_Button_Opens_Editor()
	{
		await _page!.GotoAsync("/ui/$system/config");

		// Guard the literal bug: the link must carry a real href (it was empty → dead button).
		var href = await _page.GetByTestId("config-new").GetAttributeAsync("href");
		Assert.False(string.IsNullOrEmpty(href), "config-new href must not be empty");

		await _page.GetByTestId("config-new").ClickAsync();

		await Expect(_page.GetByTestId("config-edit-form")).ToBeVisibleAsync();
		Assert.Contains("/config/editor", _page.Url, StringComparison.Ordinal);
	}

	// Regression: full create-a-binding round-trip THROUGH THE UI (REST-green ≠ UI-green).
	[Fact]
	public async Task ConfigPage_Create_Binding_Through_Ui()
	{
		await _page!.GotoAsync("/ui/$system/config");

		await _page.GetByTestId("config-new").ClickAsync();
		await Expect(_page.GetByTestId("config-edit-form")).ToBeVisibleAsync();

		await _page.GetByTestId("config-edit-path").FillAsync("e2e.create.test");
		await _page.GetByTestId("config-edit-value").FillAsync("42");
		// Tags prefilled with the mandatory ws:$system; Kind defaults to Plain.
		await _page.GetByTestId("config-save-btn").ClickAsync();

		await Expect(_page.GetByTestId("config-table")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("e2e.create.test");
	}

}
