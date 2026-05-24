using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class DataTableTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;
	string? _apiKey;

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

	async Task EnsureProjectAndKey()
	{
		await _page!.GotoAsync("/ui/admin/projects");
		var hasKpvotes = await _page.GetByTestId("project-row").Filter(new() { HasText = "kpvotes" }).CountAsync();
		if (hasKpvotes == 0)
		{
			await _page.GetByTestId("admin-project-create-workspace").SelectOptionAsync("$system");
			await _page.GetByTestId("admin-project-create-key").FillAsync("kpvotes");
			await _page.GetByTestId("admin-project-create-name").FillAsync("KpVotes");
			await _page.GetByTestId("admin-project-create-desc").FillAsync("Test");
			await _page.GetByTestId("admin-project-create-submit").ClickAsync();
			await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		}

		if (_apiKey is null)
		{
			await _page.GotoAsync("/ui/admin/projects/kpvotes");
			await _page.GetByTestId("project-key-create-scopes").ScrollIntoViewIfNeededAsync();
			await _page.GetByTestId("project-key-create-scopes").FillAsync("data:read,data:write");
			await _page.GetByTestId("project-key-create-submit").ClickAsync();
			await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

			var keyEl = _page.GetByTestId("project-key-created").Locator("code");
			await Expect(keyEl).ToBeVisibleAsync();
			_apiKey = (await keyEl.TextContentAsync())?.Trim();
		}
	}

	[Fact]
	public async Task CreateDataTable_And_Query()
	{
		await EnsureProjectAndKey();

		// Navigate to Data Tables page
		await _page!.GotoAsync("/ui/admin/projects/kpvotes/data");
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		await Expect(_page.Locator("body")).ToContainTextAsync("Data Tables");

		// Create votes_cache table
		await _page.GetByTestId("datatable-create-name").FillAsync("votes_cache");
		await _page.GetByTestId("datatable-create-columns").FillAsync(
			"""[{"name":"id","type":"TEXT","pk":true},{"name":"film_uri","type":"TEXT","notNull":true},{"name":"vote_value","type":"TEXT"},{"name":"cached_at","type":"TEXT"}]""");
		await _page.GetByTestId("datatable-create-read").CheckAsync();
		await _page.GetByTestId("datatable-create-write").CheckAsync();
		await _page.GetByTestId("datatable-create-delete").CheckAsync();
		await _page.GetByTestId("datatable-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// Table appears in list
		await Expect(_page.GetByTestId("datatable-row").Filter(new() { HasText = "votes_cache" })).ToBeVisibleAsync();

		// API query returns empty
		var resp = await _page.APIRequest.GetAsync("/api/data/votes_cache", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! },
		});
		resp.Status.Should().Be(200);
		var body = await resp.TextAsync();
		body.Should().Contain("votes_cache");
		body.Should().Contain("rows");
	}
}
