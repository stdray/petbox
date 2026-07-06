using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// authz-bypass-project-create: end-to-end regression for the cross-tenant hole that used to
// live in Admin/Projects.cshtml.cs. This drives the REAL HTTP pipeline (cookie auth + the
// "WorkspaceAdmin" authorization policy + antiforgery) rather than calling the PageModel
// directly — [Authorize] is enforced by ASP.NET Core's authorization MIDDLEWARE, which a
// direct `new ProjectsModel(db).OnPostCreateAsync(...)` call (the style used by
// WorkspaceUsersPageTests/WorkspaceDeletePageTests) never goes through, so that style of test
// could never have caught this bug. An Admin of workspace "wsa" must NOT be able to create a
// Project in workspace "wsb" (no row must land in wsb); an Admin of "wsb" must succeed.
public sealed class AdminProjectsAuthzFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public AdminProjectsAuthzFixture()
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
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		// HandleCookies=false — same reasoning as LoginAuthFixture: we thread the antiforgery
		// cookie and the auth cookie across requests by hand instead of letting the client's
		// hidden cookie container hide which one is actually gating each response.
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		await db.InsertAsync(new Workspace { Key = "wsa", Name = "Wsa", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wsb", Name = "Wsb", Description = "", CreatedAt = DateTime.UtcNow });

		// eve administers wsa ONLY.
		var eveId = await db.InsertWithInt64IdentityAsync(new User { Username = "eve", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = eveId, WorkspaceKey = "wsa", Role = WorkspaceRole.Admin });

		// bo administers wsb.
		var boId = await db.InsertWithInt64IdentityAsync(new User { Username = "bo", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = boId, WorkspaceKey = "wsb", Role = WorkspaceRole.Admin });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class AdminProjectsAuthzTests : IClassFixture<AdminProjectsAuthzFixture>
{
	readonly AdminProjectsAuthzFixture _fx;
	readonly HttpClient _client;

	public AdminProjectsAuthzTests(AdminProjectsAuthzFixture fx)
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
			["password"] = AdminProjectsAuthzFixture.Password,
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
	public async Task Admin_of_wsa_cannot_create_a_project_in_wsb()
	{
		var authCookie = await LoginAsync("eve");

		// Fetch a valid antiforgery pair from a page eve CAN reach (her own workspace's admin
		// projects page). Using a genuine, valid token here proves the POST below is blocked by
		// AUTHORIZATION — not merely rejected for a missing/invalid CSRF token.
		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/projects");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		ownPage.StatusCode.Should().Be(HttpStatusCode.OK, "eve administers wsa and must be able to load its projects page");
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		var createReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsb/projects?handler=Create");
		createReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["key"] = "intruder",
			["name"] = "Intruder Project",
			["description"] = "",
			["__RequestVerificationToken"] = token,
		});
		createReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var createResp = await _client.SendAsync(createReq);

		// AccessDeniedPath == LoginPath == "/Login" (Program.cs cookie config), so an
		// authenticated-but-unauthorized request is redirected there rather than getting a bare 403.
		createResp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the WorkspaceAdmin policy must deny eve (Admin of wsa only) acting on wsb");
		createResp.Headers.Location!.ToString().Should().Contain("/Login");

		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		db.Projects.Any(p => p.WorkspaceKey == "wsb" && p.Key == "intruder").Should().BeFalse(
			"the cross-tenant create must not have inserted a row into wsb — this is the exploit the fix closes");
	}

	// SECURITY VERIFICATION (authz-cleanup precursor): ProjectsModel.WorkspaceKey is
	// [BindProperty(SupportsGet = true)], but the WorkspaceAdmin policy's requirement handler
	// (WorkspaceRoleAuthorizationHandler) derives the target workspace from
	// HttpContext.GetRouteValue("workspaceKey") — the ROUTE, not the bound property. ASP.NET
	// Core's default composite value provider order is Form -> Route -> Query, so a form field
	// named "WorkspaceKey" can rebind the property AFTER the route-based authz check already
	// passed. This test POSTs to a route eve legitimately administers (wsa) so the policy
	// succeeds, then smuggles a form field WorkspaceKey=wsb to see whether the INSERT lands in
	// the workspace the attacker does NOT administer.
	[Fact]
	public async Task OnPostCreate_FormWorkspaceKey_CannotOverrideRouteWorkspace()
	{
		var authCookie = await LoginAsync("eve");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/projects");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		ownPage.StatusCode.Should().Be(HttpStatusCode.OK, "eve administers wsa and must be able to load its own projects page");
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		// Route workspace is wsa (policy passes for eve); form body smuggles WorkspaceKey=wsb.
		var createReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsa/projects?handler=Create");
		createReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["key"] = "pwn",
			["name"] = "pwn",
			["description"] = "",
			["WorkspaceKey"] = "wsb",
			["__RequestVerificationToken"] = token,
		});
		createReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var createResp = await _client.SendAsync(createReq);

		// The WorkspaceAdmin policy check is expected to PASS here (route == wsa, eve's own
		// workspace) — this test is about where the INSERT lands, not whether authz fires.
		createResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "eve is Admin of the route workspace (wsa), so the policy allows the POST");

		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		var landedInWsb = db.Projects.Any(p => p.WorkspaceKey == "wsb" && p.Key == "pwn");
		var landedInWsa = db.Projects.Any(p => p.WorkspaceKey == "wsa" && p.Key == "pwn");

		landedInWsb.Should().BeFalse(
			"a form-supplied WorkspaceKey must NOT override the route workspace used for the authz check — " +
			"if this fails, eve (Admin of wsa only) escalated into wsb via the bound property");
		landedInWsa.Should().BeTrue(
			"the create should still succeed, but scoped to the route workspace (wsa) eve actually administers");
	}

	[Fact]
	public async Task Admin_of_wsb_can_create_a_project_in_wsb()
	{
		var authCookie = await LoginAsync("bo");

		var pageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsb/projects");
		pageReq.Headers.Add("Cookie", authCookie);
		using var page = await _client.SendAsync(pageReq);
		page.StatusCode.Should().Be(HttpStatusCode.OK);
		var (token, afCookie) = ExtractAntiforgery(page, await page.Content.ReadAsStringAsync());

		var createReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsb/projects?handler=Create");
		createReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["key"] = "legit",
			["name"] = "Legit Project",
			["description"] = "",
			["__RequestVerificationToken"] = token,
		});
		createReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var createResp = await _client.SendAsync(createReq);

		createResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "an in-workspace admin's create must succeed");
		createResp.Headers.Location!.ToString().Should().NotContain("/Login");

		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		db.Projects.Any(p => p.WorkspaceKey == "wsb" && p.Key == "legit").Should().BeTrue(
			"bo administers wsb, so the create must land the row there");
	}
}
