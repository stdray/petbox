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

// ui-search-group-by-project (spec cross-scope-search-is-an-identifier-locator): the cross-scope
// /ui/search page's true row order is project-by-project (CrossScopeTaskSearchService.cs:126-133),
// not a cross-project relevance ranking — this drives the real Razor page over HTTP and asserts
// the grouping/bridge-link/exact-first structure the card asks for.
public sealed class SearchGroupByProjectFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public const string GroupedTerm = "xgroupbyprojectmarker";
	public const string ProjA = "xgroup-proj-a";
	public const string ProjB = "xgroup-proj-b";
	// The exact-identifier slug: pasting it must resolve through the identifier fast-path, landing
	// it in SearchModel.ExactRows — first and NEVER inside a collapsed per-project section.
	public const string ExactSlug = "xgroup-exact-slug";
	const string ExactProj = "xgroup-proj-exact";

	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public SearchGroupByProjectFixture()
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
			foreach (var key in new[] { ProjA, ProjB, ExactProj })
				if (!db.Projects.Any(p => p.Key == key))
					db.Insert(new Project { Key = key, WorkspaceKey = "$system", Name = key, Description = "" });
		}

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		// Two full-text matches in proj-a, one in proj-b — full-text-only hits, so they land in
		// Groups, never ExactRows.
		await tasks.UpsertAsync(ProjA, "work",
		[
			new NodePatch { Key = "xgroup-a-1", Title = $"{GroupedTerm} alpha one", Body = "x" },
			new NodePatch { Key = "xgroup-a-2", Title = $"{GroupedTerm} alpha two", Body = "x" },
		]);
		await tasks.UpsertAsync(ProjB, "work",
			[new NodePatch { Key = "xgroup-b-1", Title = $"{GroupedTerm} beta one", Body = "x" }]);

		// A distinct project holding a node whose KEY is the exact slug pasted below — the
		// identifier fast-path finds it regardless of title/body content.
		await tasks.UpsertAsync(ExactProj, "work",
			[new NodePatch { Key = ExactSlug, Title = "Exact target, unrelated title", Body = "x" }]);
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class SearchGroupByProjectTests(SearchGroupByProjectFixture fx) : IClassFixture<SearchGroupByProjectFixture>
{
	const string TestPassword = "test123";
	readonly HttpClient _client = fx.Client;

	// Mirrors NavHideEmptySectionsTests/SearchLocatorHonestBoundaryTests' own local copy — kept
	// local rather than shared, matching this test suite's convention of not coupling unrelated
	// fixtures together.
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
	public async Task FullTextHits_AreGroupedIntoCollapsibleProjectSections_WithNameAndCount()
	{
		using var resp = await GetAuthedAsync($"/ui/search?q={SearchGroupByProjectFixture.GroupedTerm}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Two sections, one per project, each a native <details> (collapsible), open by default.
		html.Should().Contain($"data-testid=\"search-project-group\" data-workspace=\"$system\" data-project=\"{SearchGroupByProjectFixture.ProjA}\"",
			"proj-a's two matches must land in their own section");
		html.Should().Contain($"data-testid=\"search-project-group\" data-workspace=\"$system\" data-project=\"{SearchGroupByProjectFixture.ProjB}\"",
			"proj-b's one match must land in its own section");
		html.Should().Contain("<details open", "sections are collapsible (native <details>) and open by default");

		// proj-a's header names the project and shows its count (2 rows).
		var projAIdx = html.IndexOf(SearchGroupByProjectFixture.ProjA, StringComparison.Ordinal);
		var projASection = html[projAIdx..Math.Min(projAIdx + 800, html.Length)];
		projASection.Should().Contain("data-testid=\"search-project-group-count\">2<", "proj-a section header must show its own found-count");
	}

	[Fact]
	public async Task EachSection_HasABridgeLinkToSearchWithinThatProject()
	{
		using var resp = await GetAuthedAsync($"/ui/search?q={SearchGroupByProjectFixture.GroupedTerm}");
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"search-in-project-link\"", "each section offers a way to go search inside that one project");
		html.Should().Contain($"/ui/$system/{SearchGroupByProjectFixture.ProjA}/tasks?q=", "the bridge link targets that project's own tasks page, query carried over");
		html.Should().Contain(Uri.EscapeDataString(SearchGroupByProjectFixture.GroupedTerm), "the original query is forwarded in the bridge link");
	}

	[Fact]
	public async Task ExactIdentifierHit_StaysFirstAndUngrouped_NotInsideAnyCollapsedSection()
	{
		using var resp = await GetAuthedAsync($"/ui/search?q={SearchGroupByProjectFixture.ExactSlug}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("Exact target, unrelated title", "the exact-slug hit must surface");
		html.Should().NotContain("data-testid=\"search-project-group\"",
			"a single exact-identifier hit must render ungrouped, not wrapped in a per-project section");

		// The exact hit's row must appear BEFORE any group section marker in document order (there
		// happen to be none here, but this also guards against a future regression where an exact
		// hit sits inside <details> instead of ahead of it).
		var exactIdx = html.IndexOf("Exact target, unrelated title", StringComparison.Ordinal);
		var groupIdx = html.IndexOf("data-testid=\"search-project-group\"", StringComparison.Ordinal);
		(groupIdx == -1 || exactIdx < groupIdx).Should().BeTrue("the exact hit must render ahead of any collapsed section");
	}
}
