using System.Net;
using System.Text.RegularExpressions;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;

namespace PetBox.Tests.Web;

// ui-project-task-search (spec in-project-task-search-exists): the project-wide task SEARCH
// screen that was missing entirely before this card — the cross-scope locator's own "Search in
// this project" link (Pages/Search.cshtml.cs SearchInProjectUrl) pointed at `?q=` on
// /ui/{ws}/{project}/tasks and nothing there answered it. Drives the REAL page
// (WebApplicationFactory<Program>, real HTTP + cookie login + Razor rendering) against a real
// SQLite-backed ITasksService.
public sealed class ProjectTaskSearchFixture : IAsyncLifetime
{
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Ws = "ptsearch-ws";
	public const string Proj = "ptsearch-proj";
	public const string ForeignWs = "ptsearch-foreign-ws";
	public const string ForeignProj = "ptsearch-foreign-proj";
	public const string GroupedTerm = "xptsearchmarker";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public ProjectTaskSearchFixture()
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
		// HandleCookies:false — this fixture is shared across tests that authenticate as DIFFERENT
		// users (admin vs. ptsearch-member); the default auto cookie jar would otherwise persist an
		// earlier test's login into a later test's supposedly-fresh request (WorkspaceAccessIsolationFixture's
		// own reason for the same setting).
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

		using (var db = new PetBoxDb(PetBoxDb.CreateOptions(cs)))
		{
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, Description = "", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Workspace { Key = ForeignWs, Name = ForeignWs, Description = "", CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = Proj, Description = "" });
			await db.InsertAsync(new Project { Key = ForeignProj, WorkspaceKey = ForeignWs, Name = ForeignProj, Description = "" });

			// A member restricted to Ws ONLY — proves the isolation, unlike sysadmin's free pass.
			var memberId = await db.InsertWithInt64IdentityAsync(new User
			{
				Username = "ptsearch-member", PasswordHash = TestPasswordHash, CreatedAt = DateTime.UtcNow,
			});
			await db.SeedMemberAsync(memberId, Ws, WorkspaceRole.Member);
		}

		using var scope = Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();

		// Matches spread across TWO boards of the SAME project — the search must span both
		// (project-wide, per the card: "поиска по задачам ВСЕГО проекта, все борды").
		await tasks.UpsertAsync(Proj, "work",
		[
			new NodePatch { Key = "ptsearch-w1", Title = $"{GroupedTerm} work one", Body = "x" },
			new NodePatch { Key = "ptsearch-w2", Title = $"{GroupedTerm} work two", Body = "x" },
		]);
		await tasks.UpsertAsync(Proj, "notes",
			[new NodePatch { Key = "ptsearch-n1", Title = $"{GroupedTerm} notes one", Body = "x" }]);

		// Private data in the FOREIGN project — must never leak through a crafted cross-tenant URL.
		await tasks.UpsertAsync(ForeignProj, "work",
			[new NodePatch { Key = "ptsearch-foreign-1", Title = $"{GroupedTerm} foreign secret", Body = "x" }]);
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class ProjectTaskSearchTests(ProjectTaskSearchFixture fx) : IClassFixture<ProjectTaskSearchFixture>
{
	const string TestPassword = "test123";
	readonly HttpClient _client = fx.Client;

	async Task<HttpResponseMessage> GetAuthedAsync(string url, string username = "admin")
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
			["username"] = username,
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

	static List<string> ExtractNodeKeys(string html) =>
		[.. Regex.Matches(html, "data-node-key=\"(?<k>[^\"]+)\"").Select(m => m.Groups["k"].Value)];

	static string? ExtractNextCursor(string html)
	{
		var m = Regex.Match(html, "<a href=\"(?<h>[^\"]*)\"[^>]*data-testid=\"tasks-search-next\"");
		if (!m.Success) return null;
		var href = WebUtility.HtmlDecode(m.Groups["h"].Value);
		var cm = Regex.Match(href, "[?&]cursor=(?<c>[^&]+)");
		return cm.Success ? Uri.UnescapeDataString(cm.Groups["c"].Value) : null;
	}

	[Fact]
	public async Task NoQuery_StillRendersTheBoardList_Unchanged()
	{
		using var resp = await GetAuthedAsync($"/ui/{ProjectTaskSearchFixture.Ws}/{ProjectTaskSearchFixture.Proj}/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().Contain("data-testid=\"tasks-list\"", "the pre-existing board list must still render with no q");
		html.Should().Contain("data-board-name=\"work\"");
		html.Should().Contain("data-board-name=\"notes\"");
		html.Should().NotContain("data-testid=\"tasks-search-empty\"");
	}

	[Fact]
	public async Task Query_FindsMatchesAcrossBothBoards_ThroughTheSharedTaskTable()
	{
		using var resp = await GetAuthedAsync($"/ui/{ProjectTaskSearchFixture.Ws}/{ProjectTaskSearchFixture.Proj}/tasks?q={ProjectTaskSearchFixture.GroupedTerm}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// board-view-mode-framework reuse: rows render through _TaskTable exactly like the
		// cross-scope locator's table (ShowScopeColumns:true -> testid "search-hit").
		html.Should().Contain("data-testid=\"search-hit\"");
		var keys = ExtractNodeKeys(html);
		keys.Should().BeEquivalentTo(["ptsearch-w1", "ptsearch-w2", "ptsearch-n1"],
			"the search spans EVERY board in the project, not just one");
		html.Should().Contain("data-testid=\"row-board\">work<");
		html.Should().Contain("data-testid=\"row-board\">notes<");
	}

	[Fact]
	public async Task Query_PaginatesViaCursor_VisitsEveryMatchExactlyOnce()
	{
		using var scope = fx.Factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		// Enough matches to force multiple pages at size=10.
		var term = "xptcursorwalk";
		await tasks.UpsertAsync(ProjectTaskSearchFixture.Proj, "work",
			Enumerable.Range(0, 25).Select(i => new NodePatch { Key = $"ptwalk-{i:0000}", Title = $"{term} entry {i}", Body = "x" }).ToArray());

		var seen = new List<string>();
		string? cursor = null;
		var pages = 0;
		do
		{
			var url = $"/ui/{ProjectTaskSearchFixture.Ws}/{ProjectTaskSearchFixture.Proj}/tasks?q={term}&size=10&sortBy=title"
				+ (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
			using var resp = await GetAuthedAsync(url);
			resp.StatusCode.Should().Be(HttpStatusCode.OK);
			var html = await resp.Content.ReadAsStringAsync();
			var pageKeys = ExtractNodeKeys(html).Where(k => k.StartsWith("ptwalk-", StringComparison.Ordinal)).ToList();
			pageKeys.Should().NotBeEmpty($"page {pages} must render at least one row");
			seen.AddRange(pageKeys);
			cursor = ExtractNextCursor(html);
			pages++;
			pages.Should().BeLessThan(20, "a bug here must not spin forever");
		} while (cursor is not null);

		seen.Distinct(StringComparer.Ordinal).Should().HaveCount(25, "every seeded match must be reachable exactly once");
	}

	// The bridge this card promises: the cross-scope locator's "Search in this project" link
	// (SearchInProjectUrl = Routes.ProjectTasks(ws, project) + "?q=") already existed since
	// ui-search-group-by-project — this proves it now lands on REAL matching results instead of
	// a boards-list page that silently drops the query.
	[Fact]
	public async Task TheLocatorsBridgeLink_NowLandsOnMatchingResults()
	{
		var bridgeUrl = $"/ui/{ProjectTaskSearchFixture.Ws}/{ProjectTaskSearchFixture.Proj}/tasks?q={ProjectTaskSearchFixture.GroupedTerm}";
		using var resp = await GetAuthedAsync(bridgeUrl);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		ExtractNodeKeys(html).Should().Contain(["ptsearch-w1", "ptsearch-w2", "ptsearch-n1"]);
	}

	// THE OSTOROZHNO REQUIREMENT: search must run INSIDE Tasks.cshtml.cs's two tenant-isolation
	// rubicons ([Authorize(Policy="WorkspaceViewer")] + GetInWorkspaceAsync(WorkspaceKey,
	// ProjectKey)), never beside them — a crafted URL naming a foreign workspace/project pair must
	// surface no row, `q` present or not.
	[Fact]
	public async Task MemberOfOneWorkspace_CannotReachAForeignProjectsTasks_ThroughACraftedUrl_EvenWithAQuery()
	{
		var craftedUrl = $"/ui/{ProjectTaskSearchFixture.Ws}/{ProjectTaskSearchFixture.ForeignProj}/tasks?q={ProjectTaskSearchFixture.GroupedTerm}";
		using var resp = await GetAuthedAsync(craftedUrl, username: "ptsearch-member");

		// Either denied outright (TenantEnforcementMiddleware, the first rubicon) or reached with
		// zero rows and no leaked title (GetInWorkspaceAsync's null Project, the second) — NEVER a
		// page that shows the foreign project's "foreign secret" row.
		if (resp.StatusCode == HttpStatusCode.Redirect)
		{
			resp.Headers.Location!.ToString().Should().Contain("/AccessDenied");
		}
		else
		{
			resp.StatusCode.Should().Be(HttpStatusCode.OK);
			var html = await resp.Content.ReadAsStringAsync();
			html.Should().NotContain("ptsearch-foreign-1", "the foreign project's node must never surface through a mismatched-workspace URL");
			html.Should().NotContain("foreign secret", "nor its title");
		}
	}

	[Fact]
	public async Task Sysadmin_CraftingAMismatchedWorkspaceUrl_AlsoGetsNoForeignRows()
	{
		// Sysadmin passes the AUTHORIZATION rubicon everywhere (WorkspaceViewer's free pass), so
		// this isolates the SECOND rubicon specifically: GetInWorkspaceAsync(WorkspaceKey,
		// ProjectKey) refuses a project that does not belong to the ROUTE workspace, even for sysadmin.
		var craftedUrl = $"/ui/{ProjectTaskSearchFixture.Ws}/{ProjectTaskSearchFixture.ForeignProj}/tasks?q={ProjectTaskSearchFixture.GroupedTerm}";
		using var resp = await GetAuthedAsync(craftedUrl);

		if (resp.StatusCode == HttpStatusCode.NotFound) return; // ProjectWorkspaceBindingFilter's routing 404 — also a refusal.
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().NotContain("ptsearch-foreign-1");
		html.Should().NotContain("foreign secret");
	}
}
