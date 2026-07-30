using System.Net;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Tests.Web;

// work `workspaceadmin-self-issue-admin-provision-root` / spec `access-root-explicit`: a
// WorkspaceAdmin — which every self-service tenant becomes the moment they create a workspace —
// could tick `admin:provision` on their own project's key and walk out with cross-tenant root
// (`[TenantExempt(Provisioning)]` mints into ANY project), or `deploy:write` and get the fleet.
// The server checked only that a submitted scope EXISTED (ApiKeyScopes.Validate), never that the
// caller was entitled to hand it out.
//
// EVERY TEST HERE POSTS THE FORGED FORM, and that is the entire point. The fix also filters the
// checkbox groups, but `scopes` is a `string[]` bound straight off the request body — markup that
// declines to render a checkbox stops nobody. So these drive the REAL pipeline (cookie auth +
// the WorkspaceAdmin policy + antiforgery + the page handler + AgentKeyAdminService) and submit
// scope values the page never rendered. A test that drove the PageModel directly, or that asserted
// on rendered HTML, would go green against a UI-only "fix" — which is the failure mode the card
// called out by name.
//
// The antiforgery token is always a GENUINE one, fetched from a page the actor may legitimately
// load. A refusal must be attributable to the GRANT GATE, not to a rejected CSRF token — an
// invalid-token 400 would look identical from the outside and prove nothing.
public sealed class ApiKeyScopeGrantAuthzFixture : IAsyncLifetime
{
	const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";
	public const string Password = "test123";

	// The key seeded on project `pa` that the re-scope tests aim at.
	public const string SeededKeyName = "pa worker";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;
	public string SeededKeyValue { get; private set; } = string.Empty;

	public ApiKeyScopeGrantAuthzFixture()
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
						// `root` is the env-declared bootstrap account, so CredentialAuthenticator
						// reports IsBootstrapAdmin and Login stamps IsSysAdmin=true — the claim
						// KeyIssuer keys the privileged pass on.
						//
						// PasswordHash is deliberately LEFT UNSET: AdminBootstrapper.EnsureAdminUser
						// needs BOTH settings and returns early without the hash, so it does not race
						// this fixture to seed the account. IsBootstrapAdmin only ever reads Username,
						// so the sysadmin claim lands regardless.
						["Admin:Username"] = "root",
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		// HandleCookies=false: the auth and antiforgery cookies are threaded by hand, so it stays
		// visible which one is gating each response (same reasoning as AdminProjectsAuthzFixture).
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		await db.InsertAsync(new Workspace { Key = "wsa", Name = "Wsa", Description = "", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = "pa", Name = "Pa", WorkspaceKey = "wsa" });

		// eve administers wsa — a tenant admin, and nothing more. This is the self-service onboarding
		// shape the card says the defect activates on.
		var eveId = await db.InsertWithInt64IdentityAsync(new User { Username = "eve", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });
		await db.SeedMemberAsync(eveId, "wsa", WorkspaceRole.Admin);

		// root is the sysadmin. No membership needed: WorkspaceRoleRequirement short-circuits on the
		// IsSysAdmin claim, so the operator reaches any workspace's pages.
		await db.InsertAsync(new User { Username = "root", PasswordHash = PasswordHash, CreatedAt = DateTime.UtcNow });

		// The pre-existing key the re-scope tests edit. Minted through the PRODUCTION door
		// (AgentKeyAdminService) rather than a raw insert, so it is byte-for-byte the shape a real
		// mint produces — including CreatedBy.
		var keys = scope.ServiceProvider.GetRequiredService<AgentKeyAdminService>();
		var minted = await keys.MintAsync(
			new AgentKeyMint(SeededKeyName, [ApiKeyScopes.TasksRead, ApiKeyScopes.TasksWrite], "pa"),
			KeyIssuer.System);
		SeededKeyValue = ((KeyMintResult.Minted)minted).Key.Key;
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class ApiKeyScopeGrantAuthzTests : IClassFixture<ApiKeyScopeGrantAuthzFixture>
{
	readonly ApiKeyScopeGrantAuthzFixture _fx;
	readonly HttpClient _client;

	public ApiKeyScopeGrantAuthzTests(ApiKeyScopeGrantAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	const string KeysPage = "/ui/admin/ws/wsa/projects/pa/keys";

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
			["password"] = ApiKeyScopeGrantAuthzFixture.Password,
			["__RequestVerificationToken"] = token,
		});
		req.Headers.Add("Cookie", afCookie);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, $"login as '{username}' must succeed");
		return resp.Headers.GetValues("Set-Cookie")
			.First(c => c.StartsWith(".AspNetCore.Cookies", StringComparison.OrdinalIgnoreCase))
			.Split(';')[0];
	}

	// Load the keys page as `username` and return the auth cookie plus a genuine antiforgery pair.
	async Task<(string Auth, string Token, string AfCookie, string Html)> OpenKeysPageAsync(string username)
	{
		var auth = await LoginAsync(username);
		var req = new HttpRequestMessage(HttpMethod.Get, KeysPage);
		req.Headers.Add("Cookie", auth);
		using var page = await _client.SendAsync(req);
		page.StatusCode.Should().Be(HttpStatusCode.OK, $"'{username}' must be able to load the project keys page");
		var html = await page.Content.ReadAsStringAsync();
		var (token, afCookie) = ExtractAntiforgery(page, html);
		return (auth, token, afCookie, html);
	}

	static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
		new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

	async Task<HttpResponseMessage> PostAsync(string url, string auth, string afCookie, FormUrlEncodedContent body)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
		req.Headers.Add("Cookie", $"{auth}; {afCookie}");
		return await _client.SendAsync(req);
	}

	IReadOnlyList<ApiKey> KeysOfPa()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		return [.. db.ApiKeys.Where(k => k.ProjectKey == "pa").ToList()];
	}

	// ── the exploit, on the MINT path ────────────────────────────────────────────────────────

	// THE headline case. `admin:provision` is root-equivalent: a key holding it mints keys into any
	// project, so a tenant admin issuing one to themselves escapes the tenant axis entirely.
	[Fact]
	public async Task WorkspaceAdmin_cannot_mint_a_key_with_admin_provision()
	{
		var (auth, token, afCookie, _) = await OpenKeysPageAsync("eve");

		using var resp = await PostAsync($"{KeysPage}?handler=CreateKey", auth, afCookie, Form(
			("name", "pwn-provision"),
			// Submitted DIRECTLY. The create form no longer renders this checkbox at all for eve —
			// which is exactly why the value is typed here by hand instead of being scraped from it.
			("scopes", ApiKeyScopes.AdminProvision),
			("scopes", ApiKeyScopes.TasksRead),
			("__RequestVerificationToken", token)));

		// The handler re-renders with an error rather than redirecting — a refusal the user can read,
		// not a silent drop.
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"the mint is refused in-page, not redirected as a success");

		KeysOfPa().Should().NotContain(k => k.Name == "pwn-provision",
			"NO key may be minted at all when the submission asks for a scope the issuer cannot grant — "
			+ "this is the escalation the card documents");
		KeysOfPa().Should().NotContain(k => k.Scopes.Contains(ApiKeyScopes.AdminProvision, StringComparison.Ordinal),
			"admin:provision must not reach the ApiKeys table by a workspace admin's hand");
	}

	// The second privileged scope named on the card: fleet-wide, no project/workspace scoping exists
	// on the deploy control-plane at all.
	[Fact]
	public async Task WorkspaceAdmin_cannot_mint_a_key_with_deploy_write()
	{
		var (auth, token, afCookie, _) = await OpenKeysPageAsync("eve");

		using var resp = await PostAsync($"{KeysPage}?handler=CreateKey", auth, afCookie, Form(
			("name", "pwn-deploy"),
			("scopes", ApiKeyScopes.DeployWrite),
			("__RequestVerificationToken", token)));

		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		KeysOfPa().Should().NotContain(k => k.Name == "pwn-deploy",
			"deploy:write is fleet-wide — a tenant admin must not be able to issue it");
	}

	// ── the exploit, on the RE-SCOPE path ────────────────────────────────────────────────────
	//
	// The card names the create form; the edit form was equally open and reaches the SAME table. A
	// gate on mint alone would have been a fix you could walk around by minting an innocuous key and
	// then editing it.
	[Fact]
	public async Task WorkspaceAdmin_cannot_add_a_privileged_scope_to_an_existing_key()
	{
		var (auth, token, afCookie, _) = await OpenKeysPageAsync("eve");

		using var resp = await PostAsync(
			$"{KeysPage}?handler=UpdateKeyScopes&keyValue={_fx.SeededKeyValue}", auth, afCookie, Form(
				("scopes", ApiKeyScopes.TasksRead),
				("scopes", ApiKeyScopes.AdminProvision),
				("__RequestVerificationToken", token)));

		resp.StatusCode.Should().Be(HttpStatusCode.OK, "the re-scope is refused in-page");

		var seeded = KeysOfPa().Single(k => k.Key == _fx.SeededKeyValue);
		seeded.Scopes.Should().NotContain(ApiKeyScopes.AdminProvision,
			"the edit form must not be a back door onto the scope the create form refuses");
		// The whole edit is rejected atomically — the key keeps what it had, no partial application.
		seeded.Scopes.Should().Contain(ApiKeyScopes.TasksWrite,
			"a refused edit must change nothing at all, not apply the acceptable half of the submission");
	}

	// ── the operator's path still works ──────────────────────────────────────────────────────

	[Fact]
	public async Task Sysadmin_can_mint_a_key_with_admin_provision()
	{
		var (auth, token, afCookie, _) = await OpenKeysPageAsync("root");

		using var resp = await PostAsync($"{KeysPage}?handler=CreateKey", auth, afCookie, Form(
			("name", "operator-provision"),
			("scopes", ApiKeyScopes.AdminProvision),
			("__RequestVerificationToken", token)));

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect,
			"a successful mint redirects (PRG) — if this is 200 the gate is refusing the OPERATOR, "
			+ "which would make the fix a lockout rather than a fix");
		resp.Headers.Location!.ToString().Should().NotContain("/AccessDenied");

		var minted = KeysOfPa().Single(k => k.Name == "operator-provision");
		minted.Scopes.Should().Contain(ApiKeyScopes.AdminProvision,
			"the sysadmin holds the authority the gate asks for, so the scope must land");
		minted.CreatedBy.Should().Be("user:root");
	}

	// ── the regression guard ─────────────────────────────────────────────────────────────────
	//
	// The gate must cost a workspace admin NOTHING on ordinary work. A fix that also broke the
	// legitimate project-scoped mint would be traded for an outage.
	[Fact]
	public async Task WorkspaceAdmin_can_still_mint_an_ordinary_project_scoped_key()
	{
		var (auth, token, afCookie, _) = await OpenKeysPageAsync("eve");

		using var resp = await PostAsync($"{KeysPage}?handler=CreateKey", auth, afCookie, Form(
			("name", "legit-agent"),
			("scopes", ApiKeyScopes.TasksRead),
			("scopes", ApiKeyScopes.TasksWrite),
			("scopes", ApiKeyScopes.MemoryRead),
			("__RequestVerificationToken", token)));

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "an ordinary tenant-scoped mint must still succeed");

		var minted = KeysOfPa().Single(k => k.Name == "legit-agent");
		minted.Scopes.Should().Contain(ApiKeyScopes.TasksWrite);
		minted.Scopes.Should().NotContain(ApiKeyScopes.AdminProvision);

		// spec access-attribution: the row now says who issued it. Before M049 there was no column at
		// all, so an escalation left nothing to reconstruct.
		minted.CreatedBy.Should().Be("user:eve",
			"a key minted from an admin page is attributed to the signed-in user that minted it");
	}

	// A workspace admin re-scoping their own key normally must keep working — the gate only refuses
	// the privileged ADDITION, it does not freeze the scope set.
	[Fact]
	public async Task WorkspaceAdmin_can_still_re_scope_a_key_within_the_tenant_catalog()
	{
		var (auth, token, afCookie, _) = await OpenKeysPageAsync("eve");

		using var seed = await PostAsync($"{KeysPage}?handler=CreateKey", auth, afCookie, Form(
			("name", "rescope-me"),
			("scopes", ApiKeyScopes.TasksRead),
			("__RequestVerificationToken", token)));
		seed.StatusCode.Should().Be(HttpStatusCode.Redirect);
		var keyValue = KeysOfPa().Single(k => k.Name == "rescope-me").Key;

		using var resp = await PostAsync(
			$"{KeysPage}?handler=UpdateKeyScopes&keyValue={keyValue}", auth, afCookie, Form(
				("scopes", ApiKeyScopes.TasksRead),
				("scopes", ApiKeyScopes.LogsQuery),
				("__RequestVerificationToken", token)));

		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "a tenant-scoped re-scope must still succeed");
		KeysOfPa().Single(k => k.Key == keyValue).Scopes.Should().Contain(ApiKeyScopes.LogsQuery);
	}

	// ── the cosmetic layer, asserted as cosmetic ─────────────────────────────────────────────
	//
	// Filtering the checkboxes is not the fix (every test above bypasses it), but it IS the thing
	// that stops an honest admin from being offered an affordance they would be refused for.
	[Fact]
	public async Task Create_form_offers_privileged_scopes_to_the_sysadmin_and_not_to_a_workspace_admin()
	{
		var (_, _, _, eveHtml) = await OpenKeysPageAsync("eve");
		eveHtml.Should().NotContain($"project-key-scope-{ApiKeyScopes.AdminProvision}",
			"the create form must not render a checkbox for a scope the server would refuse eve");
		eveHtml.Should().NotContain($"project-key-scope-{ApiKeyScopes.DeployWrite}");
		eveHtml.Should().Contain($"project-key-scope-{ApiKeyScopes.TasksRead}",
			"the tenant-confined catalog must still be offered in full");

		var (_, _, _, rootHtml) = await OpenKeysPageAsync("root");
		rootHtml.Should().Contain($"project-key-scope-{ApiKeyScopes.AdminProvision}",
			"the operator is entitled to the scope, so hiding it from them would be a lockout");
	}
}
