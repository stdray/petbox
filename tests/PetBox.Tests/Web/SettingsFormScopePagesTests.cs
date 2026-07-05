using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;

namespace PetBox.Tests.Web;

// Card project-log-settings-empty-form. Drives the real Sys Defaults / Workspace Defaults /
// Preferences pages end-to-end (through the actual Razor _SettingsForm / _SettingsFormFields
// partials, now backed by SettingsFormFieldSelector.GetEditable) to lock the field set each scope
// shows. The project-scope /log page is covered separately (ProjectRetentionSettingsPageTests +
// this file's redirect check) since it was already fixed by commit 9b0d4cd to skip the generic
// form entirely rather than filter it down to zero fields.
public sealed class SettingsFormScopePagesFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public SettingsFormScopePagesFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		var dbPath = Path.Combine(Path.GetTempPath(), $"petbox-settingsform-{Guid.NewGuid():N}.db");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = $"Data Source={dbPath};Cache=Shared",
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = TestPasswordHash,
				}));
			});
	}

	public Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		return Task.CompletedTask;
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class SettingsFormScopePagesTests : IClassFixture<SettingsFormScopePagesFixture>
{
	const string TestPassword = "test123";

	readonly HttpClient _client;

	public SettingsFormScopePagesTests(SettingsFormScopePagesFixture fx) => _client = fx.Client;

	[Fact]
	public async Task SysDefaults_ShowsAllThreeLogSettingsFields()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/sys/defaults", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-RetentionDays\"");
		html.Should().Contain("data-testid=\"setting-input-SystemRetainDays\"");
		html.Should().Contain("data-testid=\"setting-input-RunIntervalSeconds\"");
		html.Should().NotContain("sys-defaults-empty");
	}

	[Fact]
	public async Task WorkspaceDefaults_ShowsOnlyRetentionDays_NotSystemOnlyFields()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/ws/$system/defaults", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-RetentionDays\"");
		html.Should().NotContain("data-testid=\"setting-input-SystemRetainDays\"");
		html.Should().NotContain("data-testid=\"setting-input-RunIntervalSeconds\"");
		html.Should().NotContain("ws-defaults-empty");
	}

	[Fact]
	public async Task Preferences_ShowsThemeField_NotTheEmptyState()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/me/preferences", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-Theme\"");
		html.Should().Contain("data-testid=\"me-preferences-form-submit\"");
		html.Should().NotContain("me-preferences-form-empty");
	}

	[Fact]
	public async Task ProjectScopeLogPage_StillRedirectsToInfo_NeverRendersTheOldEmptyForm()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/ws/$system/projects/$system/log", jar);

		resp.StatusCode.Should().Be(HttpStatusCode.Found);
		resp.Headers.Location!.ToString().Should().EndWith("/info");
	}

	async Task LogInAsync(CookieJar jar)
	{
		var login = await GetAsync("/Login", jar);
		var token = ExtractToken(await login.Content.ReadAsStringAsync());
		var resp = await PostAsync("/Login?returnUrl=/", jar, new()
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = "/",
			["__RequestVerificationToken"] = token,
		});
		resp.StatusCode.Should().Be(HttpStatusCode.Found, "valid admin credentials sign in");
	}

	async Task<HttpResponseMessage> GetAsync(string url, CookieJar jar)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		jar.Apply(req);
		var resp = await _client.SendAsync(req);
		jar.Capture(resp);
		return resp;
	}

	async Task<HttpResponseMessage> PostAsync(string url, CookieJar jar, Dictionary<string, string> fields)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
		jar.Apply(req);
		var resp = await _client.SendAsync(req);
		jar.Capture(resp);
		return resp;
	}

	static string ExtractToken(string html)
	{
		var idx = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", idx, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		return html[valueStart..valueEnd];
	}

	// Minimal accumulating cookie jar (the test client has no CookieContainer): keeps the latest
	// value per cookie name across requests so anti-forgery, auth and TempData cookies all ride along.
	sealed class CookieJar
	{
		readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

		public void Capture(HttpResponseMessage resp)
		{
			if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
			foreach (var raw in values)
			{
				var pair = raw.Split(';', 2)[0];
				var eq = pair.IndexOf('=');
				if (eq <= 0) continue;
				_cookies[pair[..eq]] = pair[(eq + 1)..];
			}
		}

		public void Apply(HttpRequestMessage req)
		{
			if (_cookies.Count == 0) return;
			req.Headers.Add("Cookie", string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
		}
	}
}
