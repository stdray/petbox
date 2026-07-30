namespace PetBox.E2ETests.Infrastructure;

public sealed class WebAppFixture : IAsyncLifetime
{
	public const string AdminUsername = "admin";
	public const string AdminPassword = "test123";

	readonly KestrelAppHost _host = new();
	IPlaywright? _playwright;
	IBrowser? _browser;

	public string BaseUrl => _host.BaseUrl;
	IBrowser Browser => _browser ?? throw new InvalidOperationException("Fixture not initialized");
	public IServiceProvider Services => _host.Services;

	string _storageStatePath = "";

	public async ValueTask InitializeAsync()
	{
		FrontendBuildPreflight.EnsureBuilt();

		var hash = AdminPasswordHasher.Hash(AdminPassword);
		await _host.StartAsync(s =>
		{
			s["Admin:Username"] = AdminUsername;
			s["Admin:PasswordHash"] = hash;
			s["Features:Config"] = "true";
			s["Features:Data"] = "true";
			s["Features:Logging"] = "true";
			// The LLM admin page is feature-gated (LlmAdminUiTests drives it). No upstream is ever
			// called: the suite edits the registry and reads it back through the resolver.
			s["Features:LlmRouter"] = "true";
		});

		_playwright = await Playwright.CreateAsync();
		// PETBOX_E2E_CDP=ws://host:port/ points the suite at an external CDP browser
		// (e.g. lightpanda in WSL) instead of launching the bundled chromium.
		var cdp = Environment.GetEnvironmentVariable("PETBOX_E2E_CDP");
		_browser = string.IsNullOrEmpty(cdp)
			// Playwright's actionability checks (element "stable" across two frames) drive off
			// requestAnimationFrame. Headless Chromium 149 (bundled with Playwright 1.61) backgrounds
			// the renderer and stops producing frames even though document.visibilityState reports
			// "visible", so rAF never fires and every click times out at 30s waiting for "stable"
			// (measured 2026-07-30: 19/95 E2E red, wall-clock 11m42s vs ~1m20s; rAF probe confirmed
			// it never fires). These flags keep the renderer unthrottled so frames — and rAF — keep
			// coming. 1.59.0 did not need them; the frame-scheduling change is new in Chromium 149.
			? await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
			{
				Headless = true,
				Args = new[]
				{
					"--disable-backgrounding-occluded-windows",
					"--disable-renderer-backgrounding",
					"--disable-background-timer-throttling",
					"--disable-features=CalculateNativeWinOcclusion",
				},
			})
			: await _playwright.Chromium.ConnectOverCDPAsync(cdp);

		_storageStatePath = Path.Combine(
			Path.GetTempPath(),
			"petbox-ui-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");
		await using var seedCtx = await Browser.NewContextAsync(new BrowserNewContextOptions
		{
			BaseURL = BaseUrl,
			IgnoreHTTPSErrors = true,
		});

		var seedPage = await seedCtx.NewPageAsync();
		await seedPage.GotoAsync("/Login");
		await seedPage.GetByTestId("login-username").FillAsync(AdminUsername);
		await seedPage.GetByTestId("login-password").FillAsync(AdminPassword);
		await seedPage.GetByTestId("login-submit").ClickAsync();
		await Expect(seedPage.GetByTestId("dashboard-title")).ToBeVisibleAsync();
		await seedCtx.StorageStateAsync(new BrowserContextStorageStateOptions { Path = _storageStatePath });
	}

	public Task<IBrowserContext> NewContextAsync(bool authenticated = true) =>
		NewContextAsync(authenticated, trace: true);

	async Task<IBrowserContext> NewContextAsync(bool authenticated, bool trace)
	{
		var ctx = await Browser.NewContextAsync(new BrowserNewContextOptions
		{
			BaseURL = BaseUrl,
			IgnoreHTTPSErrors = true,
			StorageStatePath = authenticated && !string.IsNullOrEmpty(_storageStatePath) ? _storageStatePath : null,
		});

		if (trace)
			await TraceArtifact.StartAsync(ctx);
		return ctx;
	}

	public async ValueTask DisposeAsync()
	{
		if (_browser is not null)
			await _browser.CloseAsync();
		_playwright?.Dispose();
		await _host.DisposeAsync();
		if (!string.IsNullOrEmpty(_storageStatePath) && File.Exists(_storageStatePath))
			File.Delete(_storageStatePath);
	}
}
