using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// authz-bypass-project-create (phase 1 follow-up): Admin/WorkspaceUsers.cshtml.cs is gated
// [Authorize(Policy = "WorkspaceAdmin")], which checks the ROUTE {workspaceKey}. But
// OnPostAddAsync/OnPostRemoveAsync took `workspaceKey` as a PLAIN handler parameter, bound by the
// default composite provider (Form -> Route -> Query) — a POST body field named "workspaceKey"
// could override the route AFTER the policy check passed, letting an admin of wsA add/remove a
// member in wsB. Both handlers now bind `workspaceKey` via [FromRoute(Name = "workspaceKey")].
// These tests drive the real HTTP pipeline (cookie auth + antiforgery), mirroring
// AdminProjectsAuthzTests.cs.
public sealed class WorkspaceUsersAuthzFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public WorkspaceUsersAuthzFixture()
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

		// eve administers wsa ONLY.
		var eveId = await db.InsertWithInt64IdentityAsync(new User { Username = "eve3", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = eveId, WorkspaceKey = "wsa", Role = WorkspaceRole.Admin });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class WorkspaceUsersAuthzTests : IClassFixture<WorkspaceUsersAuthzFixture>
{
	readonly WorkspaceUsersAuthzFixture _fx;
	readonly HttpClient _client;

	public WorkspaceUsersAuthzTests(WorkspaceUsersAuthzFixture fx)
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
			["password"] = WorkspaceUsersAuthzFixture.Password,
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
	public async Task Admin_of_wsa_cannot_add_a_member_to_wsb()
	{
		var authCookie = await LoginAsync("eve3");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/members");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		ownPage.StatusCode.Should().Be(HttpStatusCode.OK, "eve administers wsa and must be able to load its members page");
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		var addReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsb/members?handler=Add");
		addReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["Username"] = "intruder-user",
			["Password"] = "somepassword1",
			["Role"] = "Member",
			["__RequestVerificationToken"] = token,
		});
		addReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var addResp = await _client.SendAsync(addReq);

		addResp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the WorkspaceAdmin policy must deny eve (Admin of wsa only) acting on wsb");
		addResp.Headers.Location!.ToString().Should().Contain("/AccessDenied");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.Users.Any(u => u.Username == "intruder-user").Should().BeFalse(
			"the cross-tenant add must not have created a user account, let alone a wsb membership");
	}

	// SECURITY VERIFICATION: `workspaceKey` used to be a plain handler parameter — bound by the
	// default composite provider (Form -> Route -> Query) — so a POST body field named
	// "workspaceKey" could retarget the write after the WorkspaceAdmin policy (which checks only
	// the ROUTE) had already passed. Now `[FromRoute(Name = "workspaceKey")]` pins it to the route.
	[Fact]
	public async Task OnPostAdd_FormWorkspaceKey_CannotOverrideRouteWorkspace()
	{
		var authCookie = await LoginAsync("eve3");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/members");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		// Route is wsa (policy passes for eve); form body smuggles workspaceKey=wsb.
		var addReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsa/members?handler=Add");
		addReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["Username"] = "pwn-user",
			["Password"] = "somepassword1",
			["Role"] = "Member",
			["workspaceKey"] = "wsb",
			["__RequestVerificationToken"] = token,
		});
		addReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var addResp = await _client.SendAsync(addReq);

		// The policy passes (route == wsa, eve's own workspace) — this test is about which
		// workspace the membership INSERT lands in, not whether authz fires.
		addResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "eve is Admin of the route workspace (wsa)");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var user = db.Users.FirstOrDefault(u => u.Username == "pwn-user");
		user.Should().NotBeNull("the create should still succeed — scoped to the route workspace");

		var landedInWsb = db.WorkspaceMembers.Any(m => m.UserId == user!.Id && m.WorkspaceKey == "wsb");
		var landedInWsa = db.WorkspaceMembers.Any(m => m.UserId == user!.Id && m.WorkspaceKey == "wsa");

		landedInWsb.Should().BeFalse(
			"a form-supplied workspaceKey must NOT override the route workspace used for the membership insert — " +
			"if this fails, eve (Admin of wsa only) escalated into wsb");
		landedInWsa.Should().BeTrue("the membership should land in the route workspace (wsa) eve actually administers");
	}
}
