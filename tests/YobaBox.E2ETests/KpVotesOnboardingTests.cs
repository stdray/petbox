using YobaBox.E2ETests.Infrastructure;

namespace YobaBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class KpVotesOnboardingTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
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
	public async Task CreateProject_KpVotes()
	{
		await _page!.GotoAsync("/admin/projects");

		await _page.GetByTestId("admin-project-create-key").FillAsync("kpvotes");
		await _page.GetByTestId("admin-project-create-name").FillAsync("KpVotes");
		await _page.GetByTestId("admin-project-create-desc").FillAsync("Kinopoisk → Twitter voting tracker");
		await _page.GetByTestId("admin-project-create-submit").ClickAsync();

		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var row = _page.GetByTestId("project-row").Filter(new() { HasText = "kpvotes" });
		await Expect(row).ToContainTextAsync("KpVotes");
		await Expect(row).ToContainTextAsync("Kinopoisk");
	}

	[Fact]
	public async Task CreateServices_KpVotes()
	{
		// kpvotes-net (Cron)
		await _page!.GotoAsync("/admin/projects/kpvotes");
		await _page.GetByTestId("project-service-create-key").ScrollIntoViewIfNeededAsync();
		await _page.GetByTestId("project-service-create-key").FillAsync("kpvotes-net");
		await _page.GetByTestId("project-service-create-kind").SelectOptionAsync("Cron");
		await _page.GetByTestId("project-service-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var rowNet = _page.GetByTestId("service-row").Filter(new() { HasText = "kpvotes-net" });
		await Expect(rowNet).ToContainTextAsync("Cron");

		// kpvotes-ts (PoC)
		await _page.GetByTestId("project-service-create-key").FillAsync("kpvotes-ts");
		await _page.GetByTestId("project-service-create-kind").SelectOptionAsync("PoC");
		await _page.GetByTestId("project-service-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var rowTs = _page.GetByTestId("service-row").Filter(new() { HasText = "kpvotes-ts" });
		await Expect(rowTs).ToContainTextAsync("PoC");

		// Both rows exist
		var allRows = await _page.GetByTestId("service-row").AllAsync();
		output.WriteLine($"Service rows: {allRows.Count}");
	}

	[Fact]
	public async Task CreateApiKey_And_Validate()
	{
		await SetupKpVotesProject(_page!);
		await EnsureApiKey();

		// Validate via API
		var apiResp = await _page!.APIRequest.GetAsync("/api/auth/validate", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = _kpvotesApiKey! },
		});
		apiResp.Status.Should().Be(200);
		var body = await apiResp.TextAsync();
		body.Should().Contain("kpvotes");
		body.Should().Contain("config:read");
	}

	async Task EnsureApiKey()
	{
		if (_kpvotesApiKey is not null) return;

		await _page!.GotoAsync("/admin/projects/kpvotes");
		await _page.GetByTestId("project-key-create-scopes").ScrollIntoViewIfNeededAsync();
		await _page.GetByTestId("project-key-create-scopes").FillAsync("config:read,config:write,logs:ingest,data:read,data:write");
		await _page.GetByTestId("project-key-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var keyEl = _page.GetByTestId("project-key-created").Locator("code");
		await Expect(keyEl).ToBeVisibleAsync();
		_kpvotesApiKey = (await keyEl.TextContentAsync())?.Trim();
		_kpvotesApiKey.Should().NotBeNullOrEmpty().And.StartWith("yb_key_");
	}

	static async Task SetupKpVotesProject(IPage page)
	{
		await page.GotoAsync("/admin/projects");

		// Create project if not exists
		var existing = await page.GetByTestId("project-row").CountAsync();
		if (existing <= 1) // only $system
		{
			await page.GetByTestId("admin-project-create-key").FillAsync("kpvotes");
			await page.GetByTestId("admin-project-create-name").FillAsync("KpVotes");
			await page.GetByTestId("admin-project-create-desc").FillAsync("Kinopoisk → Twitter voting tracker");
			await page.GetByTestId("admin-project-create-submit").ClickAsync();
			await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		}
	}

	[Fact]
	public async Task AddConfigBindings_And_Resolve()
	{
		await SetupKpVotesProject(_page!);
		await EnsureApiKey();
		output.WriteLine($"Using key: {_kpvotesApiKey}");

		var bindings = new (string Path, string Value, string Tags)[]
		{
			("kpvotes/kp-uri", "https://www.kinopoisk.ru/film/123", "project:kpvotes"),
			("kpvotes/votes-uri", "https://www.kinopoisk.ru/film/123/votes", "project:kpvotes"),
			("kpvotes/interval-minutes", "120", "project:kpvotes"),
			("kpvotes/user-agent", "KpVotes/1.0", "project:kpvotes"),
			("kpvotes/cache-path", "data/votes.json", "project:kpvotes"),
			("kpvotes/twitter/consumer-key", "secret123", "project:kpvotes"),
			("kpvotes/proxy/host", "proxy.corp.local", "project:kpvotes,service:kpvotes-net"),
		};

		foreach (var (path, value, tags) in bindings)
		{
			await _page!.GotoAsync("/config/edit");
			await _page.GetByTestId("config-edit-path").FillAsync(path);
			await _page.GetByTestId("config-edit-value").FillAsync(value);
			await _page.GetByTestId("config-edit-tags").FillAsync(tags);
			await _page.GetByTestId("config-save-btn").ClickAsync();
			await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
			output.WriteLine($"Created binding: {path}={value} [{tags}]");
		}

		await _page!.GotoAsync("/config");
		await _page!.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("kpvotes/interval-minutes");
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("proxy.corp.local");

		// API resolve
		var apiResp = await _page.APIRequest.GetAsync(
			"/api/config?path=kpvotes/interval-minutes&tags=project:kpvotes",
			new() { Headers = new Dictionary<string, string> { ["X-Api-Key"] = _kpvotesApiKey! } });
		var body = await apiResp.TextAsync();
		output.WriteLine($"Resolve status: {apiResp.Status}, body: {body}");
		apiResp.Status.Should().Be(200);
		body.Should().Contain("120");
	}

	string? _kpvotesApiKey;
}
