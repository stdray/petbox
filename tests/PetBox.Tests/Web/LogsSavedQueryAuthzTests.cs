using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// authz-bypass-project-create (phase 1 follow-up): Logs/Index.cshtml.cs used to carry a bare
// [Authorize] (any authenticated user, any workspace/project) AND OnPostDeleteAsync deleted a
// SavedQuery by id with NO project filter at all (`_db.SavedQueries.Where(q => q.Id ==
// savedId)`), so ANY logged-in user could delete ANY project's saved query by guessing/enumerating
// its id — worse than the usual form-override hole, since there wasn't even a bound key to
// override. The page is now [Authorize(Policy = "WorkspaceMember")] (route-checked), ProjectKeyRoute
// is [FromRoute] (route-only, unspoofable), and OnPostDeleteAsync additionally filters
// `q.ProjectKey == ProjectKeyRoute` (defense in depth — a same-workspace member of a DIFFERENT
// project must not be able to delete this project's saved query either). These tests drive the
// real HTTP pipeline (cookie auth + antiforgery), mirroring AdminProjectsAuthzTests.cs.
public sealed class LogsSavedQueryAuthzFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public long VictimSavedQueryId { get; private set; }
	public long OtherProjectSavedQueryId { get; private set; }

	public LogsSavedQueryAuthzFixture()
	{
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
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		await db.InsertAsync(new Workspace { Key = "wsa", Name = "Wsa", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wsb", Name = "Wsb", Description = "", CreatedAt = DateTime.UtcNow });

		// Two projects in wsa (own-workspace, different project) + one in wsb (cross-workspace).
		await db.InsertAsync(new Project { Key = "proja", WorkspaceKey = "wsa", Name = "ProjA", Description = "" });
		await db.InsertAsync(new Project { Key = "proja2", WorkspaceKey = "wsa", Name = "ProjA2", Description = "" });
		await db.InsertAsync(new Project { Key = "projb", WorkspaceKey = "wsb", Name = "ProjB", Description = "" });

		var now = DateTime.UtcNow;
		// The saved query under attack: belongs to proja2 (same workspace as the attacker, a
		// DIFFERENT project) — the same-workspace-other-project scenario.
		OtherProjectSavedQueryId = await db.InsertWithInt64IdentityAsync(new SavedQuery
		{
			Name = "victim-query-proja2",
			Kql = "events",
			ProjectKey = "proja2",
			CreatedAt = now,
			UpdatedAt = now,
		});
		// A second saved query belonging to projb (cross-workspace) — the classic cross-tenant case.
		VictimSavedQueryId = await db.InsertWithInt64IdentityAsync(new SavedQuery
		{
			Name = "victim-query-projb",
			Kql = "events",
			ProjectKey = "projb",
			CreatedAt = now,
			UpdatedAt = now,
		});

		// eve is a Member of wsa (has proja + proja2) but NOT of wsb.
		var eveId = await db.InsertWithInt64IdentityAsync(new User { Username = "eve4", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = eveId, WorkspaceKey = "wsa", Role = WorkspaceRole.Member });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class LogsSavedQueryAuthzTests : IClassFixture<LogsSavedQueryAuthzFixture>
{
	readonly LogsSavedQueryAuthzFixture _fx;
	readonly HttpClient _client;

	public LogsSavedQueryAuthzTests(LogsSavedQueryAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static (string Token, string Cookie) ExtractAntiforgery(HttpResponseMessage resp, string html)
	{
		var tokenStart = html.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = html.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = html.IndexOf('"', valueStart);
		var token = html[valueStart..valueEnd];
		var cookie = resp.Headers.GetValues("Set-Cookie")
			.First(c => c.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
		return (token, cookie);
	}

	async Task<string> LoginAsync(string username)
	{
		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var (token, afCookie) = ExtractAntiforgery(loginPage, loginHtml);

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = username,
			["password"] = LogsSavedQueryAuthzFixture.Password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	[Fact]
	public async Task Member_of_wsa_cannot_delete_saved_query_of_wsb_project()
	{
		var authCookie = await LoginAsync("eve4");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/wsa/proja/logs");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		ownPage.StatusCode.Should().Be(HttpStatusCode.OK, "eve is a Member of wsa and must be able to load proja's logs page");
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		var deleteReq = new HttpRequestMessage(HttpMethod.Post, "/ui/wsb/projb/logs?handler=Delete");
		deleteReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["savedId"] = _fx.VictimSavedQueryId.ToString(),
			["__RequestVerificationToken"] = token,
		});
		deleteReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var deleteResp = await _client.SendAsync(deleteReq);

		deleteResp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the WorkspaceMember policy must deny eve (Member of wsa only) acting on wsb");
		deleteResp.Headers.Location!.ToString().Should().Contain("/AccessDenied");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.SavedQueries.Any(q => q.Id == _fx.VictimSavedQueryId).Should().BeTrue(
			"the cross-tenant delete must not have removed wsb's saved query");
	}

	// SECURITY VERIFICATION (defense in depth): even scoped to eve's OWN workspace (wsa), the
	// delete must not reach a DIFFERENT project's saved query (proja2) via a route that only
	// authorizes proja. Before this fix, OnPostDeleteAsync deleted by `Id` alone with no project
	// filter at all — any workspace member of ANY project could delete any saved query anywhere by
	// id. Now the handler filters `q.ProjectKey == ProjectKeyRoute` (route-bound) too.
	[Fact]
	public async Task Member_cannot_delete_saved_query_of_a_different_project_in_the_same_workspace()
	{
		var authCookie = await LoginAsync("eve4");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/wsa/proja/logs");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		ownPage.StatusCode.Should().Be(HttpStatusCode.OK);
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		// Route is wsa/proja (policy passes — eve is a Member of wsa); the targeted saved query
		// belongs to proja2, a DIFFERENT project in the SAME workspace.
		var deleteReq = new HttpRequestMessage(HttpMethod.Post, "/ui/wsa/proja/logs?handler=Delete");
		deleteReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["savedId"] = _fx.OtherProjectSavedQueryId.ToString(),
			["__RequestVerificationToken"] = token,
		});
		deleteReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var deleteResp = await _client.SendAsync(deleteReq);

		// The policy passes (route == wsa) — this test is about the per-project scope filter,
		// not workspace-level authz.
		deleteResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.SavedQueries.Any(q => q.Id == _fx.OtherProjectSavedQueryId).Should().BeTrue(
			"a route locked to proja must not delete proja2's saved query, even within the same workspace");
	}
}
