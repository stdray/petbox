using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class ConfigResolvePriorityTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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

	[Fact]
	public async Task ConfigResolve_TagSpecificity_Wins()
	{
		await EnsureProjectAndKey();

		// Create bindings A(30), B(15), C(5) with increasing tag specificity
		await CreateConfigBinding("kpvotes/timeout", "30", "project:kpvotes");
		await CreateConfigBinding("kpvotes/timeout", "15", "project:kpvotes,service:kpvotes-bot");
		await CreateConfigBinding("kpvotes/timeout", "5", "project:kpvotes,service:kpvotes-bot,env:staging");

		// Most specific subset wins
		await AssertResolve("kpvotes/timeout", "project:kpvotes", "30");
		await AssertResolve("kpvotes/timeout", "project:kpvotes,service:kpvotes-bot", "15");
		await AssertResolve("kpvotes/timeout", "project:kpvotes,service:kpvotes-bot,env:staging", "5");

		// Fallback: service:kpvotes-web is not in any binding, but project:kpvotes alone matches A
		// (A's tags are a subset of the request). B and C are not subsets — they have service:kpvotes-bot
		// which is not in the request — so they don't match.
		await AssertResolve("kpvotes/timeout", "project:kpvotes,service:kpvotes-web", "30");

		// No binding's tags are a subset of {project:other} → 404. Old behavior (first by Id) was a bug.
		await AssertNotFound("kpvotes/timeout", "project:other");
	}

	async Task EnsureProjectAndKey()
	{
		await _page!.GotoAsync("/ui/$system");
		var hasKpvotes = await _page.GetByTestId("nav-project").Filter(new() { HasText = "kpvotes" }).CountAsync();
		if (hasKpvotes == 0)
		{
			await _page.GotoAsync("/ui/$system/projects/new");
			await _page.GetByTestId("admin-project-create-key").FillAsync("kpvotes");
			await _page.GetByTestId("admin-project-create-name").FillAsync("KpVotes");
			await _page.GetByTestId("admin-project-create-desc").FillAsync("Test project");
			await _page.GetByTestId("admin-project-create-submit").ClickAsync();
			await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		}

		if (_apiKey is null)
		{
			await _page.GotoAsync("/ui/$system/kpvotes/settings");
			await _page.GetByTestId("project-key-create-scopes").ScrollIntoViewIfNeededAsync();
			await _page.GetByTestId("project-key-create-scopes").FillAsync("config:read,config:write");
			await _page.GetByTestId("project-key-create-submit").ClickAsync();
			await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

			var keyEl = _page.GetByTestId("project-key-created").Locator("code");
			await Expect(keyEl).ToBeVisibleAsync();
			_apiKey = (await keyEl.TextContentAsync())?.Trim();
		}
	}

	async Task CreateConfigBinding(string path, string value, string tags)
	{
		await _page!.GotoAsync("/ui/$system/config/editor");
		await _page.GetByTestId("config-edit-path").FillAsync(path);
		await _page.GetByTestId("config-edit-value").FillAsync(value);
		await _page.GetByTestId("config-edit-tags").FillAsync(tags);
		await _page.GetByTestId("config-save-btn").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
	}

	async Task AssertResolve(string path, string tags, string expectedValue)
	{
		var resp = await _page!.APIRequest.GetAsync(
			$"/api/config/$system/resolve?path={path}&tags={tags}",
			new() { Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! } });
		resp.Status.Should().Be(200);
		var body = await resp.TextAsync();
		body.Should().Contain(expectedValue);
	}

	async Task AssertNotFound(string path, string tags)
	{
		var resp = await _page!.APIRequest.GetAsync(
			$"/api/config/$system/resolve?path={path}&tags={tags}",
			new() { Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! } });
		resp.Status.Should().Be(404);
	}
}
