using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

[Collection(nameof(UiCollection))]
public sealed class ApiKeyScopeTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	IBrowserContext? _ctx;
	IPage? _page;
	readonly Dictionary<string, string> _keys = [];

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

	async Task EnsureProject()
	{
		await TestWorkspace.EnsureAsync(_page!);
		await _page!.GotoAsync($"/ui/{TestWorkspace.Key}");
		var hasKpvotes = await _page.GetByTestId("nav-project-select").Locator("option[value=\"kpvotes\"]").CountAsync();
		if (hasKpvotes == 0)
		{
			await _page.GotoAsync($"/ui/admin/ws/{TestWorkspace.Key}/projects");
			await _page.GetByTestId("admin-project-create-key").FillAsync("kpvotes");
			await _page.GetByTestId("admin-project-create-name").FillAsync("KpVotes");
			await _page.GetByTestId("admin-project-create-desc").FillAsync("Test");
			await _page.GetByTestId("admin-project-create-submit").ClickAsync();
			await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		}
	}

	async Task<string> CreateApiKey(string scopes)
	{
		if (_keys.TryGetValue(scopes, out var cached))
			return cached;

		await _page!.GotoAsync($"/ui/admin/ws/{TestWorkspace.Key}/projects/kpvotes/info");
		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-create-scopes-group").ScrollIntoViewIfNeededAsync();
		foreach (var __s in scopes.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
			await _page.GetByTestId($"project-key-scope-{__s}").CheckAsync();
		await _page.GetByTestId("project-key-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var keyEl = _page.GetByTestId("project-key-created").Locator("code");
		await Expect(keyEl).ToBeVisibleAsync();
		var key = (await keyEl.TextContentAsync())?.Trim()!;
		_keys[scopes] = key;
		return key;
	}

	async Task AssertStatus(string url, string method, string scopes, int expectedStatus)
	{
		var key = await CreateApiKey(scopes);
		var headers = new Dictionary<string, string> { ["X-Api-Key"] = key };
		IAPIResponse resp = method switch
		{
			"GET" => await _page!.APIRequest.GetAsync(url, new() { Headers = headers }),
			"POST" => await _page!.APIRequest.PostAsync(url, new() { Headers = headers }),
			"DELETE" => await _page!.APIRequest.DeleteAsync(url, new() { Headers = headers }),
			_ => throw new ArgumentException($"Unknown method: {method}"),
		};
		resp.Status.Should().Be(expectedStatus);
	}

	[Fact]
	public async Task ConfigRead_CannotWriteOrDelete()
	{
		await EnsureProject();
		// /v1/conf returns 200 for any config:read key (bulk resolve, empty result is still 200).
		await AssertStatus("/v1/conf", "GET", "config:read", 200);
		await AssertStatus("/api/config/$system/bindings", "POST", "config:read", 403);
		await AssertStatus("/api/config/$system/bindings?path=scope-test&tags=project:kpvotes", "DELETE", "config:read", 403);
	}

	[Fact]
	public async Task ConfigWrite_CannotRead()
	{
		await EnsureProject();
		await AssertStatus("/v1/conf", "GET", "config:write", 403);
	}

	[Fact]
	public async Task LogsIngest_CanIngest_CannotConfigRead()
	{
		await EnsureProject();

		// Managing logs needs logs:admin; ingesting needs logs:ingest.
		var adminKey = await CreateApiKey("logs:admin");
		var createLog = await _page!.APIRequest.PostAsync("/api/logs/kpvotes/logs", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = adminKey },
			DataObject = new { name = "default" },
		});
		createLog.Status.Should().BeOneOf(201, 409);

		var key = await CreateApiKey("logs:ingest");
		var payload = """{"@t":"2025-01-01T00:00:00Z","@l":"Information","@m":"test"}""";
		var resp = await _page!.APIRequest.PostAsync("/api/ingest/kpvotes/default/clef", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = key, ["X-Service-Key"] = "kpvotes-net" },
			Data = payload,
		});
		resp.Status.Should().Be(200);

		// But cannot read config.
		await AssertStatus("/v1/conf", "GET", "logs:ingest", 403);
	}

	[Fact]
	public async Task RevokedKey_FailsValidate()
	{
		await EnsureProject();
		var key = await CreateApiKey("config:read");

		// Revoke via UI — handle confirm dialog
		_page!.Dialog += (_, d) => d.AcceptAsync();
		var revokeBtn = _page.GetByTestId("key-row").Filter(new() { HasText = key[..12] }).GetByTestId("project-key-revoke");
		await revokeBtn.ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		// Validate should fail
		var resp = await _page.APIRequest.GetAsync("/api/auth/validate", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = key },
		});
		resp.Status.Should().Be(401);
	}
}
