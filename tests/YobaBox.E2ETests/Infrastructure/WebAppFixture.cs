using Microsoft.Playwright;
using YobaBox.Core.Auth;

namespace YobaBox.E2ETests.Infrastructure;

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
			s["Features:Logging"] = "true";
		});

		_playwright = await Playwright.CreateAsync();
		_browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

		_storageStatePath = Path.Combine(
			Path.GetTempPath(),
			"yobabox-ui-state-" + Guid.NewGuid().ToString("N")[..8] + ".json");
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
