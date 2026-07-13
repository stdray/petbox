using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// Regression guard for work `nav-hide-empty-sections` (spec `nav-empty-sections-hidden`):
// a user-sidebar section that is empty AND populated only from the admin zone must be
// hidden, but a section the user/their agent can populate themselves must stay visible even
// when empty. Two dedicated projects, NEITHER of which is "$system" — Program.cs auto-seeds
// the "petbox" self-log into $system at startup whenever Features:Logging is on (regardless
// of Host:BackgroundServices), so $system itself is never a genuinely empty-logs fixture:
//   - "navhide-empty", left completely unseeded (0 logs, 0 databases, 0 task boards) — the
//     HIDE case.
//   - "navhide-full", seeded with one log and one database — the regression guard that the
//     hide logic is conditional, not "always hide", proven by the SAME sections reappearing.
public sealed class NavHideEmptySectionsFixture : IAsyncLifetime
{
	public const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string EmptyProject = "navhide-empty";
	public const string FullProject = "navhide-full";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public NavHideEmptySectionsFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Logging"] = "true",
						["Features:Data"] = "true",
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

		using var scope = Factory.Services.CreateScope();

		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			if (!db.Projects.Any(p => p.Key == EmptyProject))
				db.Insert(new Project { Key = EmptyProject, WorkspaceKey = "$system", Name = "Nav hide empty" });
			if (!db.Projects.Any(p => p.Key == FullProject))
				db.Insert(new Project { Key = FullProject, WorkspaceKey = "$system", Name = "Nav hide full" });
		}

		var logs = scope.ServiceProvider.GetRequiredService<PetBox.Log.Core.Data.ILogStore>();
		if (!await logs.ExistsAsync(FullProject, "app"))
			await logs.CreateAsync(FullProject, "app", null);

		var dbs = scope.ServiceProvider.GetRequiredService<PetBox.Data.Contract.IDataDbCatalog>();
		if (await dbs.GetAsync(FullProject, "main") is null)
			await dbs.CreateAsync(FullProject, "main", null, null);
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class NavHideEmptySectionsTests : IClassFixture<NavHideEmptySectionsFixture>
{
	readonly HttpClient _client;

	const string TestPassword = "test123";

	public NavHideEmptySectionsTests(NavHideEmptySectionsFixture fx)
	{
		_client = fx.Client;
	}

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url. Mirrors
	// ModuleViewsTests/NavTreeAndDataViewTests' own copy — kept local rather than shared to
	// avoid coupling unrelated fixtures together.
	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var token = loginHtml[valueStart..valueEnd];
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await _client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await _client.SendAsync(req);
	}

	[Fact]
	public async Task EmptyProject_HidesDatabasesAndLogsNodes_ButKeepsSelfServiceSections()
	{
		using var resp = await GetAuthedAsync($"/ui/$system/{NavHideEmptySectionsFixture.EmptyProject}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Admin-only-populated, empty: hidden.
		html.Should().NotContain("data-testid=\"nav-proj-data-node\"",
			"Databases has no databases yet and can only be created from the admin zone");
		html.Should().NotContain("data-testid=\"nav-proj-logs-node\"",
			"Logs has no logs yet and can only be created from the admin zone");

		// Self-service (agents/the user's own key can populate these), kept visible even empty.
		html.Should().Contain("data-testid=\"nav-proj-tasks\"",
			"Tasks has no boards yet but agents create them via the tasks MCP tools — stays visible as a flat link");
		html.Should().NotContain("data-testid=\"nav-proj-tasks-node\"",
			"with zero boards this must be the flat link, not the board-listing subtree");
		html.Should().Contain("data-testid=\"nav-proj-memory\"", "Memory is always a flat link, self-service");
		html.Should().Contain("data-testid=\"nav-proj-agent\"", "Sessions is always a flat link, self-service");
	}

	[Fact]
	public async Task NonEmptyProject_ShowsDatabasesAndLogsNodes()
	{
		using var resp = await GetAuthedAsync($"/ui/$system/{NavHideEmptySectionsFixture.FullProject}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"nav-proj-data-node\"",
			"one database exists — the hide rule is conditional on emptiness, not a blanket hide");
		html.Should().Contain("data-testid=\"nav-proj-logs-node\"",
			"one log exists — same regression guard for Logs");
	}
}
