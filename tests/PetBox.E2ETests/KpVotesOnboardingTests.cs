using PetBox.E2ETests.Infrastructure;

namespace PetBox.E2ETests;

// User story coverage:
// S-3 (set up a new pet from scratch): project create → service add → API key mint
// S-4 (add config bindings for a pet): bindings stored, resolve via API
// S-2 (diagnose a misbehaving pet via logs): ingest CLEF, KQL query
// S-1 (glance at all pets): Status page shows the new pet
[Collection(nameof(UiCollection))]
public sealed class KpVotesOnboardingTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = TestWorkspace.Key;
	const string Pet = "kpvotes";

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
	public async Task S3_CreateProject_AppearsInSidebarAndStatus()
	{
		await EnsureProject();

		// Sidebar reflects the new project.
		await _page!.GotoAsync($"/ui/{Ws}");
		var projectLink = _page.GetByTestId("nav-project").Filter(new() { HasText = Pet });
		await Expect(projectLink).ToBeVisibleAsync();

		// Status page lists the project card.
		var card = _page.GetByTestId("dashboard-project-card").Filter(new() { HasText = Pet });
		await Expect(card).ToBeVisibleAsync();
		await Expect(card).ToContainTextAsync("KpVotes");
	}

	[Fact]
	public async Task S3_MintKey()
	{
		await EnsureProject();
		await EnsureApiKey();

		// API key validates via /api/auth/validate.
		var apiResp = await _page!.APIRequest.GetAsync("/api/auth/validate", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! },
		});
		apiResp.Status.Should().Be(200);
		var body = await apiResp.TextAsync();
		body.Should().Contain(Pet);
		body.Should().Contain("config:read");
	}

	[Fact]
	public async Task S4_AddConfigBindings_ResolveViaApi()
	{
		await EnsureProject();
		await EnsureApiKey();

		var bindings = new (string Path, string Value, string Tags)[]
		{
			("kpvotes/interval-minutes", "120", $"project:{Pet}"),
			("kpvotes/proxy/host", "proxy.corp.local", $"project:{Pet},service:kpvotes-net"),
		};

		foreach (var (path, value, tags) in bindings)
		{
			await CreateBindingViaApi(path, value, tags);
		}

		// /v1/conf bulk-resolve — workspace derived from the key's project. Project-level binding present.
		var resp = await _page!.APIRequest.GetAsync(
			$"/v1/conf?project={Pet}",
			new() { Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! } });
		resp.Status.Should().Be(200);
		(await resp.TextAsync()).Should().Contain("120");

		// Service-specific binding is visible when the request carries the matching service tag.
		var resp2 = await _page.APIRequest.GetAsync(
			$"/v1/conf?project={Pet}&service=kpvotes-net",
			new() { Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! } });
		resp2.Status.Should().Be(200);
		(await resp2.TextAsync()).Should().Contain("proxy.corp.local");

		// From a different service the override is NOT in the resolved set (subset semantics).
		var resp3 = await _page.APIRequest.GetAsync(
			$"/v1/conf?project={Pet}&service=kpvotes-ts",
			new() { Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! } });
		resp3.Status.Should().Be(200);
		(await resp3.TextAsync()).Should().NotContain("proxy.corp.local");

		// Project-config UI page renders, filter chip shows project:{key}.
		await _page.GotoAsync($"/ui/{Ws}/{Pet}/config");
		await Expect(_page.GetByTestId("config-project-filter")).ToContainTextAsync($"project:{Pet}");
		await Expect(_page.GetByTestId("config-table")).ToContainTextAsync("kpvotes/interval-minutes");
	}

	[Fact]
	public async Task S2_IngestLogs_QueryViaKql()
	{
		await EnsureProject();
		await EnsureApiKey();

		// Logs are explicit now — create the default log before ingesting into it.
		var createLog = await _page!.APIRequest.PostAsync($"/api/logs/{Pet}/logs", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! },
			DataObject = new { name = "default" },
		});
		createLog.Status.Should().BeOneOf(201, 409);

		var clefEvents = new (string Level, string Message, string Svc)[]
		{
			("Information", "Starting scrape", "kpvotes-net"),
			("Warning", "Proxy timeout, retrying", "kpvotes-net"),
			("Error", "Rate limit 429", "kpvotes-net"),
		};

		for (var i = 0; i < clefEvents.Length; i++)
		{
			var (level, msg, svc) = clefEvents[i];
			var ts = DateTime.UtcNow.AddSeconds(-clefEvents.Length + i).ToString("O");
			var payload = $"{{\"@t\":\"{ts}\",\"@l\":\"{level}\",\"@m\":\"{msg}\"}}";

			var resp = await _page!.APIRequest.PostAsync($"/api/ingest/{Pet}/default/clef", new()
			{
				Headers = new Dictionary<string, string>
				{
					["X-Api-Key"] = _apiKey!,
					["X-Service-Key"] = svc,
					["Content-Type"] = "application/vnd.serilog.clef",
				},
				Data = payload,
			});
			resp.Status.Should().Be(200);
		}

		// Project Logs page.
		await _page!.GotoAsync($"/ui/{Ws}/{Pet}/logs");
		await Expect(_page.GetByTestId("proj-tabs")).ToBeVisibleAsync();

		// Filter to Error level.
		await _page.GetByTestId("kql-input").FillAsync("events | where Level == 4");
		await _page.GetByTestId("kql-apply").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
		await Expect(_page.Locator("body")).ToContainTextAsync("Rate limit");
	}

	[Fact]
	public async Task NavigateBetweenTabs_HighlightsActive()
	{
		await EnsureProject();

		await _page!.GotoAsync($"/ui/{Ws}/{Pet}");
		await Expect(_page.GetByTestId("proj-tab-dashboard")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(".*tab-active.*"));

		await _page.GetByTestId("proj-tab-logs").ClickAsync();
		await Expect(_page.GetByTestId("proj-tab-logs")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(".*tab-active.*"));

		await _page.GetByTestId("proj-tab-config").ClickAsync();
		await Expect(_page.GetByTestId("proj-tab-config")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(".*tab-active.*"));

		// "Admin" link leaves the project tab strip and enters the admin area.
		await _page.GetByTestId("proj-admin-link").ClickAsync();
		await _page.WaitForURLAsync($"**/ui/admin/ws/{Ws}/projects/{Pet}/info");
	}

	// --- Setup helpers ------------------------------------------------------

	async Task EnsureProject()
	{
		await TestWorkspace.EnsureAsync(_page!);
		await _page!.GotoAsync($"/ui/{Ws}");
		var existing = _page.GetByTestId("nav-project").Filter(new() { HasText = Pet });
		if (await existing.CountAsync() > 0) return;

		await _page.GotoAsync($"/ui/admin/ws/{Ws}/projects");
		await _page.GetByTestId("admin-project-create-key").FillAsync(Pet);
		await _page.GetByTestId("admin-project-create-name").FillAsync("KpVotes");
		await _page.GetByTestId("admin-project-create-desc").FillAsync("Kinopoisk → Twitter voting tracker");
		await _page.GetByTestId("admin-project-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
	}

	async Task EnsureApiKey()
	{
		if (_apiKey is not null) return;

		await _page!.GotoAsync($"/ui/admin/ws/{Ws}/projects/{Pet}/info");
		await _page.GetByTestId("project-key-create-scopes-group").ScrollIntoViewIfNeededAsync();
		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-key-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-scope-config:read").CheckAsync();

		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-key-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-scope-config:write").CheckAsync();

		await _page.GetByTestId("project-key-create-name").FillAsync($"e2e-key-{System.Guid.NewGuid():N}");
		await _page.GetByTestId("project-key-scope-logs:ingest").CheckAsync();
		await _page.GetByTestId("project-key-scope-logs:admin").CheckAsync();
		await _page.GetByTestId("project-key-create-submit").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		var keyEl = _page.GetByTestId("project-key-created").Locator("code");
		await Expect(keyEl).ToBeVisibleAsync();
		_apiKey = (await keyEl.TextContentAsync())?.Trim();
		_apiKey.Should().NotBeNullOrEmpty().And.StartWith("yb_key_");
	}

	async Task CreateBindingViaApi(string path, string value, string tags)
	{
		var resp = await _page!.APIRequest.PostAsync($"/api/config/{Ws}/bindings", new()
		{
			Headers = new Dictionary<string, string> { ["X-Api-Key"] = _apiKey! },
			DataObject = new { path, value, tags = $"ws:{Ws},{tags}" },
		});
		resp.Status.Should().Be(200);
	}
}
