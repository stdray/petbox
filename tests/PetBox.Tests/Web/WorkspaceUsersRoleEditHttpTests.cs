using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// workspace-member-role-edit: HTTP-level coverage for OnPostSetRoleAsync — the parts a page-model
// unit test cannot see: the WorkspaceAdmin POLICY gate (non-admin callers denied), the sysadmin
// free pass, and that a role change takes effect on the VERY NEXT request through the same cookie
// (WorkspaceClaimsRefresher rebuilds yb:ws_roles from the DB per request — no re-login).
public sealed class WorkspaceUsersRoleEditFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public WorkspaceUsersRoleEditFixture()
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
						// Bootstrap admin → the sysadmin free-pass this suite must cover.
						["Admin:Username"] = "sa",
						["Admin:PasswordHash"] = PasswordHash,
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

		await db.InsertAsync(new Workspace { Key = "wsr", Name = "Wsr", Description = "", CreatedAt = DateTime.UtcNow });

		var adminId = await db.InsertWithInt64IdentityAsync(new User { Username = "wsr-admin", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = adminId, WorkspaceKey = "wsr", Role = WorkspaceRole.Admin });

		var memberId = await db.InsertWithInt64IdentityAsync(new User { Username = "wsr-member", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = memberId, WorkspaceKey = "wsr", Role = WorkspaceRole.Member });

		var viewerId = await db.InsertWithInt64IdentityAsync(new User { Username = "wsr-viewer", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = viewerId, WorkspaceKey = "wsr", Role = WorkspaceRole.Viewer });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class WorkspaceUsersRoleEditHttpTests : IClassFixture<WorkspaceUsersRoleEditFixture>
{
	readonly WorkspaceUsersRoleEditFixture _fx;
	readonly HttpClient _client;

	public WorkspaceUsersRoleEditHttpTests(WorkspaceUsersRoleEditFixture fx)
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
		var (token, afCookie) = ExtractAntiforgery(loginPage, await loginPage.Content.ReadAsStringAsync());

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = username,
			["password"] = WorkspaceUsersRoleEditFixture.Password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	async Task<HttpResponseMessage> GetAsync(string url, string authCookie)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie);
		return await _client.SendAsync(req);
	}

	// Sets userId's role on wsr via the SetRole handler, authenticated as `actorCookie`. The
	// antiforgery token is fetched from "/ui/wsr" (WorkspaceViewer-gated — Viewer/Member/Admin/
	// sysadmin can all reach it) rather than the members page itself, because a non-admin actor
	// (Member/Viewer) is exactly the case under test here and cannot GET the Admin-gated members
	// page at all. "/" would redirect (not render) for any of these actors since they all have an
	// active workspace. The antiforgery pair is session-scoped, not page-scoped, so a token minted
	// on one page validates a POST anywhere.
	async Task<HttpResponseMessage> SetRoleAsync(string actorCookie, long userId, WorkspaceRole role)
	{
		var membersUrl = "/ui/admin/ws/wsr/members";
		var pageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/wsr");
		pageReq.Headers.Add("Cookie", actorCookie);
		using var page = await _client.SendAsync(pageReq);
		var (token, afCookie) = ExtractAntiforgery(page, await page.Content.ReadAsStringAsync());

		var req = new HttpRequestMessage(HttpMethod.Post, $"{membersUrl}?handler=SetRole");
		req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["userId"] = userId.ToString(),
			["Role"] = role.ToString(),
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", $"{actorCookie}; {afCookie}");
		return await _client.SendAsync(req);
	}

	long UserId(string username)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return db.Users.First(u => u.Username == username).Id;
	}

	WorkspaceRole RoleOf(string username, string workspaceKey)
	{
		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var userId = db.Users.First(u => u.Username == username).Id;
		return db.WorkspaceMembers.First(m => m.UserId == userId && m.WorkspaceKey == workspaceKey).Role;
	}

	[Fact]
	public async Task Admin_changes_a_members_role_and_the_db_reflects_it()
	{
		var adminCookie = await LoginAsync("wsr-admin");
		var targetId = UserId("wsr-viewer");

		using var resp = await SetRoleAsync(adminCookie, targetId, WorkspaceRole.Member);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the admin's role change must succeed");
		resp.Headers.Location!.ToString().Should().NotContain("/AccessDenied");
		RoleOf("wsr-viewer", "wsr").Should().Be(WorkspaceRole.Member);

		// restore for other tests sharing the fixture
		using var restore = await SetRoleAsync(adminCookie, targetId, WorkspaceRole.Viewer);
		restore.StatusCode.Should().Be(HttpStatusCode.Redirect);
	}

	// The claims-refresh guarantee: a member demoted to Viewer loses Member+ access on the very
	// next request over the SAME cookie — no sign-out/sign-in round trip. /ui/admin/ws/{key} is
	// gated [Authorize(Policy = "WorkspaceMember")], i.e. Member or Admin, never Viewer.
	[Fact]
	public async Task Demoted_member_loses_member_only_access_on_the_very_next_request()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var demoteeId = await db.InsertWithInt64IdentityAsync(
			new User { Username = "wsr-demotee", PasswordHash = WorkspaceUsersRoleEditFixture.PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = demoteeId, WorkspaceKey = "wsr", Role = WorkspaceRole.Member });

		var demoteeCookie = await LoginAsync("wsr-demotee");
		using (var before = await GetAsync("/ui/admin/ws/wsr", demoteeCookie))
			before.StatusCode.Should().Be(HttpStatusCode.OK, "a Member must be able to open the WorkspaceMember-gated page");

		var adminCookie = await LoginAsync("wsr-admin");
		using (var demote = await SetRoleAsync(adminCookie, demoteeId, WorkspaceRole.Viewer))
			demote.StatusCode.Should().Be(HttpStatusCode.Redirect);

		using var after = await GetAsync("/ui/admin/ws/wsr", demoteeCookie);
		after.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the SAME cookie must lose Member+ access immediately — WorkspaceClaimsRefresher rebuilds " +
			"the role claim from the DB every request, no re-login required");
		after.Headers.Location!.ToString().Should().Contain("/AccessDenied");
	}

	[Fact]
	public async Task Member_cannot_call_SetRole_on_their_own_workspace()
	{
		var memberCookie = await LoginAsync("wsr-member");
		var targetId = UserId("wsr-viewer");
		var roleBefore = RoleOf("wsr-viewer", "wsr");

		using var resp = await SetRoleAsync(memberCookie, targetId, WorkspaceRole.Admin);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the WorkspaceAdmin policy must deny a non-admin");
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied");
		RoleOf("wsr-viewer", "wsr").Should().Be(roleBefore, "a denied call must not mutate the role");
	}

	[Fact]
	public async Task Viewer_cannot_call_SetRole_on_their_own_workspace()
	{
		var viewerCookie = await LoginAsync("wsr-viewer");
		var targetId = UserId("wsr-member");
		var roleBefore = RoleOf("wsr-member", "wsr");

		using var resp = await SetRoleAsync(viewerCookie, targetId, WorkspaceRole.Admin);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the WorkspaceAdmin policy must deny a non-admin");
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied");
		RoleOf("wsr-member", "wsr").Should().Be(roleBefore, "a denied call must not mutate the role");
	}

	[Fact]
	public async Task Sysadmin_can_change_roles_in_a_workspace_it_does_not_belong_to()
	{
		var sysadminCookie = await LoginAsync("sa");
		var targetId = UserId("wsr-member");

		using var resp = await SetRoleAsync(sysadminCookie, targetId, WorkspaceRole.Viewer);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "sysadmin implicitly administers every workspace");
		resp.Headers.Location!.ToString().Should().NotContain("/AccessDenied");
		RoleOf("wsr-member", "wsr").Should().Be(WorkspaceRole.Viewer);

		// restore for other tests sharing the fixture
		using var restore = await SetRoleAsync(sysadminCookie, targetId, WorkspaceRole.Member);
		restore.StatusCode.Should().Be(HttpStatusCode.Redirect);
	}
}
