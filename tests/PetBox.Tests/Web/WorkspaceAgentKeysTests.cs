using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// workspace-admin-owns-keys.
//
// The workspace admin could MINT a project key (ProjectConnect, WorkspaceAdmin) but list+revoke
// were SysAdmin-only — a leaked key left them powerless over their own project. /ui/admin/ws/{ws}/
// agent-keys gives them both, confined to the projects of THEIR workspace.
//
// The load-bearing test is Ws_admin_cannot_revoke_a_key_of_another_workspace: revoke is addressed
// by the key VALUE, so filtering the rendered list is NOT a guard — a forged POST naming another
// tenant's key must still fail, and the key must survive it (workspace-access-isolation's IDOR,
// one layer down).
public sealed class WorkspaceAgentKeysFixture : IAsyncLifetime
{
	public const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public WorkspaceAgentKeysFixture()
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
						// /v1/conf is the live probe a revoked key must stop passing.
						["Features:Config"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = PasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
		{
			AllowAutoRedirect = false,
			HandleCookies = false,
		});

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		await db.InsertAsync(new Workspace { Key = "wska", Name = "Wska", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = "wskb", Name = "Wskb", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = "projka", WorkspaceKey = "wska", Name = "A", Description = "" });
		await db.InsertAsync(new Project { Key = "projkb", WorkspaceKey = "wskb", Name = "B", Description = "" });

		await AddUser(db, "admin-a", "wska", WorkspaceRole.Admin);
		await AddUser(db, "admin-b", "wskb", WorkspaceRole.Admin);
		await AddUser(db, "member-a", "wska", WorkspaceRole.Member);
		await AddUser(db, "viewer-a", "wska", WorkspaceRole.Viewer);

		// A key nobody revokes: the antiforgery token lives in the per-row revoke form, so both
		// pages must have at least one row for a test to be able to forge a POST at all.
		await MintKeyAsync("projka", "decoy-key-wska");
	}

	static async Task AddUser(PetBoxDb db, string username, string ws, WorkspaceRole role)
	{
		var id = await db.InsertWithInt64IdentityAsync(new User
		{
			Username = username,
			PasswordHash = PasswordHash,
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new WorkspaceMember { UserId = id, WorkspaceKey = ws, Role = role });
	}

	// A fresh key per test — the revoke tests are destructive.
	public async Task<string> MintKeyAsync(string projectKey, string name)
	{
		var key = $"yb_key_{Guid.NewGuid():N}";
		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new ApiKey
		{
			Key = key,
			ProjectKey = projectKey,
			Scopes = "config:read",
			Name = name,
			CreatedAt = DateTime.UtcNow,
		});
		return key;
	}

	public bool KeyExists(string key)
	{
		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return db.ApiKeys.Any(k => k.Key == key);
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class WorkspaceAgentKeysTests : IClassFixture<WorkspaceAgentKeysFixture>
{
	readonly WorkspaceAgentKeysFixture _fx;
	readonly HttpClient _client;

	public WorkspaceAgentKeysTests(WorkspaceAgentKeysFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	const string WsaKeys = "/ui/admin/ws/wska/agent-keys";
	const string SysKeys = "/ui/admin/sys/agent-keys";

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

		var req = new HttpRequestMessage(HttpMethod.Post, "/Login")
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["username"] = username,
				["password"] = WorkspaceAgentKeysFixture.Password,
				["__RequestVerificationToken"] = token,
			}),
		};
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

	// The antiforgery token is taken from a page the caller MAY open (their own workspace's key
	// page) — the forged-revoke test then aims that valid token at someone else's key, which is
	// exactly what an attacker can do. The guard must not be the antiforgery token.
	async Task<HttpResponseMessage> PostRevokeAsync(string pageUrl, string authCookie, string key)
	{
		var getReq = new HttpRequestMessage(HttpMethod.Get, pageUrl);
		getReq.Headers.Add("Cookie", authCookie);
		using var getResp = await _client.SendAsync(getReq);
		getResp.StatusCode.Should().Be(HttpStatusCode.OK, "the attacker's own page must render — that is where the token comes from");
		var (token, afCookie) = ExtractAntiforgery(getResp, await getResp.Content.ReadAsStringAsync());

		var req = new HttpRequestMessage(HttpMethod.Post, $"{pageUrl}?handler=Revoke")
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["key"] = key,
				["__RequestVerificationToken"] = token,
			}),
		};
		req.Headers.Add("Cookie", $"{authCookie}; {afCookie}");
		return await _client.SendAsync(req);
	}

	async Task<HttpStatusCode> ProbeApiAsync(string apiKey)
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Add("X-Api-Key", apiKey);
		using var resp = await _client.SendAsync(req);
		return resp.StatusCode;
	}

	[Fact]
	public async Task Ws_admin_sees_only_the_keys_of_their_own_workspace()
	{
		await _fx.MintKeyAsync("projka", "key-of-wska");
		await _fx.MintKeyAsync("projkb", "key-of-wskb");
		await _fx.MintKeyAsync("*", "cross-project-key");

		var auth = await LoginAsync("admin-a");
		using var resp = await GetAsync(WsaKeys, auth);

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("key-of-wska");
		html.Should().NotContain("key-of-wskb", "another workspace's keys are not this admin's business");
		html.Should().NotContain("cross-project-key", "a wildcard key spans every workspace — only a sysadmin owns it");
	}

	// THE test: the list filter is cosmetic; the guard must live in the POST handler.
	[Fact]
	public async Task Ws_admin_cannot_revoke_a_key_of_another_workspace()
	{
		var victim = await _fx.MintKeyAsync("projkb", "victim-key-wskb");

		var auth = await LoginAsync("admin-a");
		using var resp = await PostRevokeAsync(WsaKeys, auth, victim);

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a forged POST naming another workspace's key must not be honoured (404 — not even a hint that it exists)");
		_fx.KeyExists(victim).Should().BeTrue("the key must SURVIVE the forged revoke");
		(await ProbeApiAsync(victim)).Should().Be(HttpStatusCode.OK, "and must still authenticate");
	}

	[Fact]
	public async Task Ws_admin_cannot_revoke_a_cross_project_key()
	{
		var wildcard = await _fx.MintKeyAsync("*", "victim-wildcard-key");

		var auth = await LoginAsync("admin-a");
		using var resp = await PostRevokeAsync(WsaKeys, auth, wildcard);

		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		_fx.KeyExists(wildcard).Should().BeTrue("a `*` key belongs to no single workspace — a ws admin must not kill it");
	}

	[Fact]
	public async Task Ws_admin_revokes_their_own_key_and_it_stops_authenticating()
	{
		var mine = await _fx.MintKeyAsync("projka", "own-key-wska");
		(await ProbeApiAsync(mine)).Should().Be(HttpStatusCode.OK, "the key works before the revoke");

		var auth = await LoginAsync("admin-a");
		using var resp = await PostRevokeAsync(WsaKeys, auth, mine);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "PRG back to the key list");
		resp.Headers.Location!.ToString().Should().NotContain("/AccessDenied");
		_fx.KeyExists(mine).Should().BeFalse();
		(await ProbeApiAsync(mine)).Should().Be(HttpStatusCode.Unauthorized,
			"the whole point: the leaked key is DEAD, without a sysadmin");
	}

	[Theory]
	[InlineData("member-a")]
	[InlineData("viewer-a")]
	public async Task Non_admin_members_of_wska_are_denied_the_key_page(string username)
	{
		var auth = await LoginAsync(username);
		using var resp = await GetAsync(WsaKeys, auth);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied",
			"keys are an ADMIN surface — a member/viewer must not even see them");
	}

	[Fact]
	public async Task Admin_of_wskb_is_denied_wska_key_page()
	{
		var auth = await LoginAsync("admin-b");
		using var resp = await GetAsync(WsaKeys, auth);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied",
			"admin of another workspace is not an admin here");
	}

	[Fact]
	public async Task Sysadmin_still_sees_and_revokes_every_key_on_the_system_page()
	{
		var foreignKey = await _fx.MintKeyAsync("projkb", "sysadmin-target-key");

		var auth = await LoginAsync("admin");
		using (var page = await GetAsync(SysKeys, auth))
		{
			page.StatusCode.Should().Be(HttpStatusCode.OK);
			(await page.Content.ReadAsStringAsync()).Should().Contain("sysadmin-target-key",
				"the sysadmin view is server-wide — it lists keys of every workspace");
		}

		using var revoke = await PostRevokeAsync(SysKeys, auth, foreignKey);
		revoke.StatusCode.Should().Be(HttpStatusCode.Redirect);
		_fx.KeyExists(foreignKey).Should().BeFalse("sysadmin may revoke anything, anywhere");
	}

	[Fact]
	public async Task Ws_key_page_is_not_reachable_by_a_non_member()
	{
		var auth = await LoginAsync("admin-a");
		using var resp = await GetAsync("/ui/admin/ws/wskb/agent-keys", auth);

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
		resp.Headers.Location!.ToString().Should().Contain("/AccessDenied");
	}
}
