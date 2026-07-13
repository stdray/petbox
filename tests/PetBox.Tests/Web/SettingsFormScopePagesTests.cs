using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Settings;

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
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
					["Host:BackgroundServices"] = "false",
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
	readonly WebApplicationFactory<Program> _factory;

	public SettingsFormScopePagesTests(SettingsFormScopePagesFixture fx)
	{
		_client = fx.Client;
		_factory = fx.Factory;
	}

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
	public async Task SysDefaults_ShowsSessionFullScanSystemToggle_NotProjectToggle()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/sys/defaults", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-SystemEnabled\"");
		html.Should().NotContain("data-testid=\"setting-input-ProjectEnabled\"");
	}

	[Fact]
	public async Task WorkspaceDefaults_ShowsAllLogIngestionDashboardFields_UnderInterimB()
	{
		// INTERIM decision B (SettingsScopePolicy): TopLevel is no longer a write-depth ceiling for
		// System/Workspace/Project, so System-only-TopLevel fields (SystemRetainDays,
		// RunIntervalSeconds, and the whole of IngestionSettings/DashboardSettings) are now
		// project/workspace-overridable and show here too. SessionFullScanSettings stays excluded —
		// both its fields are HasMinScope-pinned away from Workspace.
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/ws/$system/defaults", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-RetentionDays\"");
		html.Should().Contain("data-testid=\"setting-input-SystemRetainDays\"");
		html.Should().Contain("data-testid=\"setting-input-RunIntervalSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-ChannelCapacity\"");
		html.Should().Contain("data-testid=\"setting-input-MaxBatchSize\"");
		html.Should().Contain("data-testid=\"setting-input-HealthPollIntervalSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-RequestTimeoutSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-StaleSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-HealthRetentionDays\"");
		html.Should().NotContain("data-testid=\"setting-input-SystemEnabled\"");
		html.Should().NotContain("data-testid=\"setting-input-ProjectEnabled\"");
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
	public async Task ProjectSettingsAdmin_ShowsProjectFullScanToggle_NotSystemToggle()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/ws/$system/projects/$system/settings", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-ProjectEnabled\"");
		html.Should().NotContain("data-testid=\"setting-input-SystemEnabled\"");
		html.Should().NotContain("proj-settings-empty");
	}

	[Fact]
	public async Task ProjectSettingsAdmin_AlsoShowsLogIngestionDashboardFields_UnderInterimB()
	{
		// INTERIM decision B: the uniform Records registry (SettingsScopePolicy.Records) means the
		// project-scope page is no longer limited to SessionFullScanSettings.ProjectEnabled — every
		// generic record now offers itself down to Project scope too.
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/ws/$system/projects/$system/settings", jar);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"setting-input-RetentionDays\"");
		html.Should().Contain("data-testid=\"setting-input-SystemRetainDays\"");
		html.Should().Contain("data-testid=\"setting-input-RunIntervalSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-ChannelCapacity\"");
		html.Should().Contain("data-testid=\"setting-input-MaxBatchSize\"");
		html.Should().Contain("data-testid=\"setting-input-HealthPollIntervalSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-RequestTimeoutSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-StaleSeconds\"");
		html.Should().Contain("data-testid=\"setting-input-HealthRetentionDays\"");
	}

	[Fact]
	public async Task AllThreeScopePages_ShowIdenticalUniformFieldSet()
	{
		// The generic-page requirement: System/Workspace/Project all present the SAME
		// Log/Ingestion/Dashboard field catalog (SettingsScopePolicy.Records driving all three).
		// The HasMinScope-pinned records are deliberately excluded from this comparison — their
		// fields stay pinned to their own single scope, by design (SessionFullScanSettings; and
		// LlmRegistryInheritanceSettings, whose System tap and Workspace opt-out are two
		// independent, non-cascading switches).
		var jar = new CookieJar();
		await LogInAsync(jar);

		var sysHtml = await (await GetAsync("/ui/admin/sys/defaults", jar)).Content.ReadAsStringAsync();
		var wsHtml = await (await GetAsync("/ui/admin/ws/$system/defaults", jar)).Content.ReadAsStringAsync();
		var projHtml = await (await GetAsync("/ui/admin/ws/$system/projects/$system/settings", jar)).Content.ReadAsStringAsync();

		var sysFields = UniformFieldTestIds(sysHtml);
		var wsFields = UniformFieldTestIds(wsHtml);
		var projFields = UniformFieldTestIds(projHtml);

		sysFields.Should().NotBeEmpty();
		wsFields.Should().BeEquivalentTo(sysFields);
		projFields.Should().BeEquivalentTo(sysFields);
	}

	// Every HasMinScope-pinned field: rendered on exactly ONE scope page, so it can never be part of
	// the uniform field set the three pages must agree on.
	static readonly string[] PinnedFieldNames =
		["SystemEnabled", "ProjectEnabled", "SystemShared", "WorkspaceInherits"];

	static HashSet<string> UniformFieldTestIds(string html)
	{
		var found = new HashSet<string>(StringComparer.Ordinal);
		const string marker = "data-testid=\"setting-input-";
		var idx = 0;
		while (true)
		{
			idx = html.IndexOf(marker, idx, StringComparison.Ordinal);
			if (idx < 0) break;
			var start = idx + marker.Length;
			var end = html.IndexOf('"', start);
			var name = html[start..end];
			if (Array.IndexOf(PinnedFieldNames, name) < 0)
				found.Add(name);
			idx = end;
		}
		return found;
	}

	[Fact]
	public async Task ProjectSettingsAdmin_Save_PersistsProjectEnabled_ReadableAtProjectScope()
	{
		var jar = new CookieJar();
		await LogInAsync(jar);

		var getResp = await GetAsync("/ui/admin/ws/$system/projects/$system/settings", jar);
		var token = ExtractToken(await getResp.Content.ReadAsStringAsync());

		var postResp = await PostAsync("/ui/admin/ws/$system/projects/$system/settings?handler=Save", jar, new()
		{
			["recordType"] = "SessionFullScanSettings",
			["ProjectEnabled"] = "true",
			["__RequestVerificationToken"] = token,
		});
		postResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await postResp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"proj-settings-success\"");

		using var scope = _factory.Services.CreateScope();
		var resolver = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
		var resolved = await resolver.GetAsync<SessionFullScanSettings>(Scope.Project, "$system");
		resolved.ProjectEnabled.Should().BeTrue();
	}

	[Fact]
	public async Task ProjectSettingsAdmin_Save_DashboardTimeout_OverridesWorkspaceAndSystem_DeeperWins()
	{
		// The core B guarantee: a System-TopLevel setting (DashboardSettings.RequestTimeoutSeconds),
		// now project-overridable under SettingsScopePolicy, still resolves deeper-wins once
		// overridden at each level — this exercises SettingsResolver's (untouched) cascade, proving
		// the policy widening actually composes with it.
		using (var seed = _factory.Services.CreateScope())
		{
			var resolver = seed.ServiceProvider.GetRequiredService<ISettingsResolver>();
			var sysOld = await resolver.GetAsync<DashboardSettings>(Scope.System, "$");
			await resolver.SetAsync(Scope.System, "$", sysOld with { RequestTimeoutSeconds = 11 }, sysOld, updatedBy: null);
			var wsOld = await resolver.GetAsync<DashboardSettings>(Scope.Workspace, "$system");
			await resolver.SetAsync(Scope.Workspace, "$system", wsOld with { RequestTimeoutSeconds = 22 }, wsOld, updatedBy: null);
		}

		var jar = new CookieJar();
		await LogInAsync(jar);

		var getResp = await GetAsync("/ui/admin/ws/$system/projects/$system/settings", jar);
		var token = ExtractToken(await getResp.Content.ReadAsStringAsync());

		var postResp = await PostAsync("/ui/admin/ws/$system/projects/$system/settings?handler=Save", jar, new()
		{
			["recordType"] = "DashboardSettings",
			["RequestTimeoutSeconds"] = "33",
			["__RequestVerificationToken"] = token,
		});
		postResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await postResp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"proj-settings-success\"");

		using var verify = _factory.Services.CreateScope();
		var verifyResolver = verify.ServiceProvider.GetRequiredService<ISettingsResolver>();

		(await verifyResolver.GetAsync<DashboardSettings>(Scope.Project, "$system")).RequestTimeoutSeconds.Should().Be(33);
		(await verifyResolver.GetAsync<DashboardSettings>(Scope.Workspace, "$system")).RequestTimeoutSeconds.Should().Be(22);
		(await verifyResolver.GetAsync<DashboardSettings>(Scope.System, "$")).RequestTimeoutSeconds.Should().Be(11);
	}

	[Fact]
	public async Task ProjectScopeLogPage_StillRedirectsToSettings_NeverRendersTheOldEmptyForm()
	{
		// admin-routes-and-pages item 3: log retention moved from the Info page to the generic
		// Settings page, so the legacy /log redirect target moved with it.
		var jar = new CookieJar();
		await LogInAsync(jar);

		var resp = await GetAsync("/ui/admin/ws/$system/projects/$system/log", jar);

		resp.StatusCode.Should().Be(HttpStatusCode.Found);
		resp.Headers.Location!.ToString().Should().EndWith("/settings");
	}

	async Task LogInAsync(CookieJar jar)
	{
		var login = await GetAsync("/Login", jar);
		// The class's HttpClient keeps its cookie container across tests, so by the second test the
		// session is already signed in — and /Login now REDIRECTS an authenticated visitor to "/"
		// instead of re-rendering the form (auth-denied-and-empty-state). Nothing left to do.
		if (login.StatusCode == HttpStatusCode.Redirect) return;
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
