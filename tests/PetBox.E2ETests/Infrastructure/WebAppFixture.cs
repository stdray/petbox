using Microsoft.Playwright;
using PetBox.Core.Auth;

namespace PetBox.E2ETests.Infrastructure;

public sealed class WebAppFixture : IAsyncLifetime
{
	public const string AdminUsername = "admin";
	public const string AdminPassword = "test123";

	readonly KestrelAppHost _host = new();
	IPlaywright? _playwright;
	IBrowser? _browser;

	public string BaseUrl => _host.BaseUrl;
	public IBrowser Browser => _browser ?? throw new InvalidOperationException("Fixture not initialized");
	public IServiceProvider Services => _host.Services;

	string _storageStatePath = "";

	public async Task InitializeAsync()
	{
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
			? await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true })
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

	public async Task<IBrowserContext> NewContextAsync(bool authenticated, bool trace)
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

	public async Task DisposeAsync()
	{
		if (_browser is not null)
			await _browser.CloseAsync();
		_playwright?.Dispose();
		await _host.DisposeAsync();
		if (!string.IsNullOrEmpty(_storageStatePath) && File.Exists(_storageStatePath))
			File.Delete(_storageStatePath);
	}
}
