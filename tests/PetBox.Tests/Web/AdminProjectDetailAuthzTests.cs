using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// authz-bypass-project-create (phase 1 follow-up): Admin/ProjectDetail.cshtml.cs used to carry a
// bare [Authorize] (any authenticated user, any workspace) AND bound WorkspaceKey/ProjectKey via
// [BindProperty(SupportsGet = true)] — form-overridable even after a policy check. Both holes are
// now closed ([Authorize(Policy = "WorkspaceAdmin")] + [FromRoute] on both keys). These tests drive
// the real HTTP pipeline (cookie auth + antiforgery), mirroring AdminProjectsAuthzTests.cs.
public sealed class AdminProjectDetailAuthzFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public AdminProjectDetailAuthzFixture()
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
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		await db.InsertAsync(new Workspace { Key = "wsa", Name = "Wsa", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wsb", Name = "Wsb", Description = "", CreatedAt = DateTime.UtcNow });

		await db.InsertAsync(new Project { Key = "proja", WorkspaceKey = "wsa", Name = "ProjA", Description = "" });
		await db.InsertAsync(new Project { Key = "projb", WorkspaceKey = "wsb", Name = "ProjB", Description = "" });

		// eve administers wsa ONLY.
		var eveId = await db.InsertWithInt64IdentityAsync(new User { Username = "eve2", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new WorkspaceMember { UserId = eveId, WorkspaceKey = "wsa", Role = WorkspaceRole.Admin });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class AdminProjectDetailAuthzTests : IClassFixture<AdminProjectDetailAuthzFixture>
{
	readonly AdminProjectDetailAuthzFixture _fx;
	readonly HttpClient _client;

	public AdminProjectDetailAuthzTests(AdminProjectDetailAuthzFixture fx)
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
			["password"] = AdminProjectDetailAuthzFixture.Password,
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
	public async Task Admin_of_wsa_cannot_delete_project_in_wsb()
	{
		var authCookie = await LoginAsync("eve2");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/projects/proja/info");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		ownPage.StatusCode.Should().Be(HttpStatusCode.OK, "eve administers wsa and must be able to load proja's info page");
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		var deleteReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsb/projects/projb/info?handler=Delete");
		deleteReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["__RequestVerificationToken"] = token,
		});
		deleteReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var deleteResp = await _client.SendAsync(deleteReq);

		deleteResp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the WorkspaceAdmin policy must deny eve (Admin of wsa only) acting on wsb");
		deleteResp.Headers.Location!.ToString().Should().Contain("/Login");

		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		db.Projects.Any(p => p.Key == "projb").Should().BeTrue("the cross-tenant delete must not have removed wsb's project");
	}

	[Fact]
	public async Task Admin_of_wsa_cannot_create_apikey_in_wsb_project()
	{
		var authCookie = await LoginAsync("eve2");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/projects/proja/info");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		var createKeyReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsb/projects/projb/info?handler=CreateKey");
		createKeyReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["name"] = "intruder-key",
			["scopes"] = "tasks:read",
			["__RequestVerificationToken"] = token,
		});
		createKeyReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var createResp = await _client.SendAsync(createKeyReq);

		createResp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"the WorkspaceAdmin policy must deny eve (Admin of wsa only) acting on wsb");
		createResp.Headers.Location!.ToString().Should().Contain("/Login");

		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		db.ApiKeys.Any(k => k.ProjectKey == "projb" && k.Name == "intruder-key").Should().BeFalse(
			"the cross-tenant create must not have minted a key for wsb's project");
	}

	// SECURITY VERIFICATION: ProjectKey/WorkspaceKey used to be [BindProperty(SupportsGet = true)]
	// on this page too (same class of hole Admin/Projects.cshtml.cs had) — a POST to a route eve
	// legitimately administers (wsa/proja) could smuggle form fields ProjectKey=projb /
	// WorkspaceKey=wsb to retarget the DB write at a project she does not administer. Now both are
	// [FromRoute] — the route wins regardless of what the form carries.
	[Fact]
	public async Task OnPostCreateKey_FormProjectKeyAndWorkspaceKey_CannotOverrideRoute()
	{
		var authCookie = await LoginAsync("eve2");

		var ownPageReq = new HttpRequestMessage(HttpMethod.Get, "/ui/admin/ws/wsa/projects/proja/info");
		ownPageReq.Headers.Add("Cookie", authCookie);
		using var ownPage = await _client.SendAsync(ownPageReq);
		var (token, afCookie) = ExtractAntiforgery(ownPage, await ownPage.Content.ReadAsStringAsync());

		// Route is wsa/proja (policy passes for eve); form body smuggles ProjectKey=projb and
		// WorkspaceKey=wsb, the workspace/project eve does NOT administer.
		var createKeyReq = new HttpRequestMessage(HttpMethod.Post, "/ui/admin/ws/wsa/projects/proja/info?handler=CreateKey");
		createKeyReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["name"] = "pwn-key",
			["scopes"] = "tasks:read",
			["ProjectKey"] = "projb",
			["WorkspaceKey"] = "wsb",
			["__RequestVerificationToken"] = token,
		});
		createKeyReq.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		using var createResp = await _client.SendAsync(createKeyReq);

		// The policy passes (route == wsa, eve's own workspace) — this test is about where the
		// INSERT lands, not whether authz fires.
		createResp.StatusCode.Should().Be(HttpStatusCode.Redirect, "eve is Admin of the route workspace (wsa)");

		using var scope = _fx.Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		db.ApiKeys.Any(k => k.ProjectKey == "projb" && k.Name == "pwn-key").Should().BeFalse(
			"a form-supplied ProjectKey must NOT override the route project used for the DB write");
		db.ApiKeys.Any(k => k.ProjectKey == "proja" && k.Name == "pwn-key").Should().BeTrue(
			"the create should still succeed, but scoped to the route project (proja) eve actually administers");
	}
}
