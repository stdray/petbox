using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.Tests.Web;

// ui-search-locator-honest-boundary (spec cross-scope-search-is-an-identifier-locator): the
// cross-scope /ui/search page must state its MaxResults cap as a known, direct fact — never a
// hedge ("possibly" truncated) — and name how to get the full answer when it's reached (paste
// the exact slug/NodeId, or narrow to a search within one project). Drives the real Razor page
// over HTTP (not CrossScopeTaskSearchService directly — CrossScopeTaskSearchServiceTests' job)
// because the wording under test lives in Search.cshtml, not the service.
public sealed class SearchLocatorHonestBoundaryFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	// A distinctive term seeded into exactly enough nodes to make the fan-out hit MaxResults=50
	// EXACTLY: CeilingProjects projects x MaxFullTextPerProject(5) matching nodes each = 50 unique
	// NodeIds, so the merge cap (`if (merged.Count >= MaxResults) break`) is reached on the nose —
	// not a guess about how many there "really" are, a deliberately engineered exact hit.
	public const string CeilingTerm = "xlocatorceilingmarker";
	// A term seeded into a single node in a single project — nowhere near the cap.
	public const string FewTerm = "xlocatorfewmarker";
	const int CeilingProjects = 10;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public SearchLocatorHonestBoundaryFixture()
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
					["Features:Tasks"] = "true",
					["Admin:Username"] = "admin",
					["Admin:PasswordHash"] = TestPasswordHash,
				}));
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();

		using (var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open())
		{
			for (var i = 0; i < CeilingProjects; i++)
			{
				var key = $"xceil-proj-{i}";
				if (!db.Projects.Any(p => p.Key == key))
					db.Insert(new Project { Key = key, WorkspaceKey = "$system", Name = key, Description = "" });
			}
			const string fewKey = "xfew-proj-0";
			if (!db.Projects.Any(p => p.Key == fewKey))
				db.Insert(new Project { Key = fewKey, WorkspaceKey = "$system", Name = fewKey, Description = "" });
		}

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		for (var i = 0; i < CeilingProjects; i++)
		{
			var proj = $"xceil-proj-{i}";
			var nodes = Enumerable.Range(0, 5)
				.Select(j => new NodePatch { Key = $"xceil-{i}-{j}", Title = $"{CeilingTerm} entry {i}-{j}", Body = "x" })
				.ToArray();
			await tasks.UpsertAsync(proj, "work", nodes);
		}
		await tasks.UpsertAsync("xfew-proj-0", "work",
			[new NodePatch { Key = "xfew-0", Title = $"{FewTerm} entry", Body = "x" }]);
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class SearchLocatorHonestBoundaryTests(SearchLocatorHonestBoundaryFixture fx) : IClassFixture<SearchLocatorHonestBoundaryFixture>
{
	const string TestPassword = "test123";
	readonly HttpClient _client = fx.Client;

	// Logs in (redirect-driven cookie auth) and returns the authenticated response for url.
	// Mirrors NavHideEmptySectionsTests/ModuleViewsTests' own local copy — kept local rather than
	// shared, matching this test suite's own convention of not coupling unrelated fixtures.
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
	public async Task ExactlyAtMaxResults_StatesTheCeilingDirectly_NamesBothNarrowingActions()
	{
		using var resp = await GetAuthedAsync($"/ui/search?q={SearchLocatorHonestBoundaryFixture.CeilingTerm}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"search-locator-ceiling\"",
			"the fan-out hit exactly MaxResults=50 unique matching nodes across 10 projects");

		html.ToLowerInvariant().Should().NotContain("possibly",
			"the boundary is a known, stated fact — never hedged as a guess word");

		html.Should().Contain("50", "the exact known ceiling, stated as a number");
		html.Should().Contain("slug", "one named narrowing action: paste the exact identifier");
		html.Should().Contain("project", "the other named narrowing action: search within one project");
	}

	[Fact]
	public async Task BelowMaxResults_NoCeilingBannerShown()
	{
		using var resp = await GetAuthedAsync($"/ui/search?q={SearchLocatorHonestBoundaryFixture.FewTerm}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain(SearchLocatorHonestBoundaryFixture.FewTerm, "the single seeded match must still surface");
		html.Should().NotContain("data-testid=\"search-locator-ceiling\"",
			"well under the cap — nothing was cut off, so no boundary claim is made");
	}
}
