using System.Net;
using System.Net.Http.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Contract;
using PetBox.Tests.Mcp;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Web;

// authz-cleanup-phase2-rest: ShareApi.CreateShareAsync (POST /api/share) carried a bare
// .RequireAuthorization() — proves SOME authenticated identity, not that it's authorized for the
// project named in the JSON body (`req.ProjectKey`, fully attacker-controlled). The resulting share
// token is later served ANONYMOUSLY at GetTsvAsync (token-is-the-credential, by design — left
// untouched), so any authenticated key could mint an export link for ANOTHER project's log data.
// Fixed by verifying req.ProjectKey against the caller's project claim via ProjectScope.Authorizes,
// the same pattern SessionApi's endpoints use. Drives the REST endpoint directly with X-Api-Key,
// mirroring ConfV1Tests' WebApplicationFactory style.
public sealed class ShareApiAuthzFixture : IAsyncLifetime
{
	// The workspace both projects live in — the UI test below needs it in the page route.
	public const string Workspace = "$system";

	// The named log ProjA owns, so the Logs page renders past its `NoLogs` early-return and the
	// share modal (with the Revoke button) is actually in the markup.
	public const string LogName = "petbox";

	public const string ProjA = "shareauthzproja";
	public const string ProjB = "shareauthzprojb";
	public const string KeyA = "yb_key_authz_share_a";
	public const string KeyB = "yb_key_authz_share_b";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public ShareApiAuthzFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
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
						["Features:Logging"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = "$system", Name = "ProjA" });
		await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = "$system", Name = "ProjB" });
		await db.InsertAsync(new ApiKey { Key = KeyA, ProjectKey = ProjA, Scopes = "logs:query", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = KeyB, ProjectKey = ProjB, Scopes = "logs:query", CreatedAt = DateTime.UtcNow });

		// Its files land under the fixture's OWN temp connection-string directory (Program.cs derives
		// the data dir from the connection string), so this touches no shared dev data.
		var logs = scope.ServiceProvider.GetRequiredService<ILogStore>();
		await logs.CreateAsync(ProjA, LogName, "share-revoke ui coverage");
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class ShareApiAuthzTests : IClassFixture<ShareApiAuthzFixture>
{
	readonly ShareApiAuthzFixture _fx;
	readonly HttpClient _client;

	public ShareApiAuthzTests(ShareApiAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static HttpRequestMessage ShareReq(string apiKey, string projectKey, int ttlMinutes = 60)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/share");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = JsonContent.Create(new
		{
			projectKey,
			kql = "events | take 10",
			ttlMinutes,
			columns = (string[]?)null,
			modes = (Dictionary<string, int>?)null,
			logName = (string?)null,
		});
		return req;
	}

	static HttpRequestMessage DeleteReq(string apiKey, string token, string projectKey)
	{
		var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/share/{token}");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = JsonContent.Create(new { projectKey });
		return req;
	}

	async Task<string> CreateShareAsync(string apiKey, string projectKey, int ttlMinutes = 60)
	{
		using var resp = await _client.SendAsync(ShareReq(apiKey, projectKey, ttlMinutes));
		resp.StatusCode.Should().Be(HttpStatusCode.OK, "share creation must succeed in test setup");
		var body = await resp.Content.ReadFromJsonAsync<ShareCreatedResponse>();
		return body!.Id;
	}

	[Fact]
	public async Task CreateShare_OwnProject_Succeeds()
	{
		using var resp = await _client.SendAsync(ShareReq(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA));
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"a key authorized for its own project must be able to mint a share link for it");
	}

	[Fact]
	public async Task CreateShare_ForeignProject_Returns403_AndDoesNotCreateLink()
	{
		using var resp = await _client.SendAsync(ShareReq(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjB));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a key authorized only for project A must not mint a share link exporting project B's logs");

		using var scope = _fx.Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		db.ShareLinks.Any(s => s.ProjectKey == ShareApiAuthzFixture.ProjB).Should().BeFalse(
			"no share token must have been minted for the foreign project");
	}

	// spec share-link-revocable, criterion (a): the owner revokes and the token immediately stops
	// serving its TSV, well before ExpiresAt — a 90-day TTL here is deliberately far from expiry,
	// pinning "revoke does not depend on TTL" at the REST layer (the unit-level equivalent lives in
	// ShareLinkDirectoryTests.DeleteAsync_OwnProject_HardDeletesTheRow_EvenFarFromExpiry).
	[Fact]
	public async Task DeleteShare_OwnProject_RevokesImmediately_SubsequentTsvReadIsRefused()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA, ttlMinutes: 60 * 24 * 90);

		using var deleteResp = await _client.SendAsync(
			DeleteReq(ShareApiAuthzFixture.KeyA, token, ShareApiAuthzFixture.ProjA));
		deleteResp.StatusCode.Should().Be(HttpStatusCode.OK,
			"a key authorized for the share's own project must be able to revoke it");

		using var tsvResp = await _client.GetAsync($"/api/share/{token}/tsv");
		tsvResp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a revoked token must stop serving its export immediately, not wait for ExpiresAt");
	}

	// spec share-link-revocable, criterion (b): a caller honestly authorized for ITS OWN project
	// (ProjB, via KeyB) cannot revoke a token that belongs to a DIFFERENT project (ProjA) merely by
	// knowing its value. This is the sharper half of the trap the card calls out — the PEP
	// ([TenantFrom(BodyField,"projectKey")]) only proves the caller owns SOME project, so the
	// ownership check has to happen again against the STORED share.ProjectKey
	// (IShareLinkDirectory.DeleteAsync's job). The token must remain readable afterwards.
	[Fact]
	public async Task DeleteShare_ForeignProject_IsRefused_TokenSurvivesAndStaysReadable()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA);

		using var deleteResp = await _client.SendAsync(
			DeleteReq(ShareApiAuthzFixture.KeyB, token, ShareApiAuthzFixture.ProjB));
		deleteResp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a key authorized only for project B must not be able to revoke project A's share token");

		using var tsvResp = await _client.GetAsync($"/api/share/{token}/tsv");
		tsvResp.StatusCode.Should().Be(HttpStatusCode.OK,
			"the token must still be usable — a foreign-tenant revoke attempt must not remove it");
	}

	// spec share-link-revocable, criterion (b), the other half: a caller who LIES about which project
	// they are (claiming ProjA in the body, the share's real owner) but is not actually authorized for
	// it gets the same refusal CreateShareAsync already gives a lying caller — reuses the PEP's
	// existing 403, not a new shape.
	[Fact]
	public async Task DeleteShare_ClaimingUnauthorizedProject_Returns403_SameAsCreate()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA);

		using var deleteResp = await _client.SendAsync(
			DeleteReq(ShareApiAuthzFixture.KeyB, token, ShareApiAuthzFixture.ProjA));
		deleteResp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a key not authorized for the claimed projectKey must be refused before the handler runs, " +
			"exactly like CreateShareAsync's cross-tenant refusal");
	}

	// spec share-link-revocable, criterion (d): revoking a token that never existed must answer
	// IDENTICALLY to revoking a foreign-tenant token (see DeleteShare_ForeignProject_IsRefused above)
	// — both 404 — so a caller cannot use the revoke endpoint to probe whether some token exists under
	// a project it does not own.
	[Fact]
	public async Task DeleteShare_UnknownToken_Returns404_SameShapeAsForeignProject()
	{
		using var deleteResp = await _client.SendAsync(
			DeleteReq(ShareApiAuthzFixture.KeyA, "tok-never-existed-at-all", ShareApiAuthzFixture.ProjA));
		deleteResp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"an unknown token must not be distinguishable from a foreign-tenant token");
	}

	// ── CARD share-link-revocation-finish, POINT 3: the ANONYMOUS HTML PAGE ─────────────────────
	//
	// The stand check after share-link-no-revocation only ever exercised the REST read path
	// (GET /api/share/{token}/tsv). /ui/share/{token} is a SECOND anonymous reader of the same row —
	// ShareModel.OnGetAsync calls the same IShareLinkDirectory.FindAsync — and "it must follow
	// automatically from the hard delete" was an argument, not a measurement. This measures it.
	//
	// The BEFORE half is not decoration: without it, an "after" that finds share-notfound proves
	// nothing (a page that never resolved the token would look identical). So the same token is
	// fetched twice and the two renders must DIFFER.
	[Fact]
	public async Task DeleteShare_OwnProject_AlsoStopsTheAnonymousHtmlPage_NotOnlyTheTsvEndpoint()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA);

		using var beforeResp = await _client.GetAsync($"/ui/share/{token}");
		beforeResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var before = await beforeResp.Content.ReadAsStringAsync();
		before.Should().NotContain("share-notfound",
			"the control: before the revoke this page RESOLVES the token — otherwise the assertion after "
			+ "the revoke would be green for a page that never found anything");
		before.Should().Contain(ShareApiAuthzFixture.ProjA,
			"the resolved page renders the stored link's own project, so the row was really read");

		using var deleteResp = await _client.SendAsync(
			DeleteReq(ShareApiAuthzFixture.KeyA, token, ShareApiAuthzFixture.ProjA));
		deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

		using var afterResp = await _client.GetAsync($"/ui/share/{token}");
		afterResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var after = await afterResp.Content.ReadAsStringAsync();
		after.Should().Contain("share-notfound",
			"the browser half of the grant must stop resolving the token the moment it is revoked — the "
			+ "hard delete removes the row both anonymous readers share");
		after.Should().NotContain("events | take 10",
			"and it must not still be rendering the revoked link's stored KQL");
	}

	// ── THE MCP VERB (mcp:share_revoke) ──────────────────────────────────────────────────────────
	//
	// The same act on the agent-facing surface. The tool method is driven directly with the host's
	// REAL IShareLinkDirectory (the tenant PEP sits OUTSIDE the tool body now and is asserted
	// separately below — see McpTenantPep), and the assertion is deliberately made on the two
	// ANONYMOUS READ PATHS rather than on the return value: a verb that answered {revoked:true} while
	// the token kept serving is the one failure worth catching here.
	[Fact]
	public async Task ShareRevokeTool_OwnProject_StopsBothTheTsvEndpointAndTheHtmlPage()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA,
			ttlMinutes: 60 * 24 * 90);

		using (var live = await _client.GetAsync($"/api/share/{token}/tsv"))
			live.StatusCode.Should().Be(HttpStatusCode.OK, "the control: the token serves before the revoke");

		using var scope = _fx.Factory.Services.CreateScope();
		var result = await ShareTools.RevokeAsync(
			scope.ServiceProvider.GetRequiredService<IShareLinkDirectory>(),
			ShareApiAuthzFixture.ProjA, token);

		result.Revoked.Should().BeTrue();
		result.Token.Should().Be(token, "the ack names what it revoked");

		using var tsvResp = await _client.GetAsync($"/api/share/{token}/tsv");
		tsvResp.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"share_revoke must withdraw the export immediately, exactly like its REST twin — and well "
			+ "before the 90-day TTL this token was minted with");

		using var pageResp = await _client.GetAsync($"/ui/share/{token}");
		(await pageResp.Content.ReadAsStringAsync()).Should().Contain("share-notfound",
			"and the anonymous HTML page must stop resolving it too — one revoke, both readers");
	}

	// The half the PEP cannot cover: a caller honestly authorized for ProjB, naming ProjB, aimed at
	// ProjA's token. The tenant gate is satisfied, so only DeleteAsync's (token, projectKey) match
	// stands between that caller and someone else's link.
	[Fact]
	public async Task ShareRevokeTool_ForeignProjectsToken_IsNotFound_AndTheTokenKeepsWorking()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA);

		using var scope = _fx.Factory.Services.CreateScope();
		var shareLinks = scope.ServiceProvider.GetRequiredService<IShareLinkDirectory>();

		var act = async () => await ShareTools.RevokeAsync(shareLinks, ShareApiAuthzFixture.ProjB, token);
		await act.Should().ThrowAsync<InvalidOperationException>()
			.WithMessage("share link not found",
				"ProjB's caller must not be able to revoke ProjA's token by knowing its value");

		using var tsvResp = await _client.GetAsync($"/api/share/{token}/tsv");
		tsvResp.StatusCode.Should().Be(HttpStatusCode.OK, "the foreign revoke attempt must not have landed");
	}

	// …and a token that never existed answers IDENTICALLY, so the verb cannot be used to learn which
	// tokens exist under a project the caller cannot reach (the REST twin's criterion (d), on MCP).
	[Fact]
	public async Task ShareRevokeTool_UnknownToken_AnswersIdenticallyToAForeignOne()
	{
		using var scope = _fx.Factory.Services.CreateScope();
		var shareLinks = scope.ServiceProvider.GetRequiredService<IShareLinkDirectory>();

		var act = async () => await ShareTools.RevokeAsync(
			shareLinks, ShareApiAuthzFixture.ProjB, "tok-never-existed-at-all");
		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("share link not found");
	}

	// The tenant axis of the new surface, asked where the decision actually lives: the MCP PEP, over
	// the declaration reflected off the shipped assembly. A missing or wrong [TenantFrom] on
	// ShareTools fails HERE, naming the tool — the body has no tenant check of its own to fall back
	// on, so nothing else in this file would notice.
	[Fact]
	public async Task ShareRevokeTool_DeclaresItsTenant_SoThePepRefusesAForeignProject()
	{
		await McpTenantPep.RefusesAsync(TestProjectCatalog.Instance, "share_revoke",
			ShareApiAuthzFixture.ProjA, claim: ShareApiAuthzFixture.ProjB);

		await McpTenantPep.AllowsAsync(TestProjectCatalog.Instance, "share_revoke",
			ShareApiAuthzFixture.ProjA, claim: ShareApiAuthzFixture.ProjA);
	}

	// ── THE UI BUTTON ────────────────────────────────────────────────────────────────────────────
	//
	// Card point 2: DELETE /api/share/{token} shipped live and unreachable from the product — the
	// share modal offered Copy and nothing else, so revoking meant hand-writing a curl. This asserts
	// the control is IN the modal the Share button opens, and that the page ships the script that
	// makes it call the revoke endpoint (a button with no handler would satisfy a markup-only check
	// while changing nothing for the user).
	[Fact]
	public async Task LogsPage_ShareModal_CarriesARevokeControl_WiredToTheRevokeEndpoint()
	{
		using var client = _fx.Factory.CreateClient(
			new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
		await LoginAsync(client);

		using var page = await client.GetAsync(
			$"/ui/{ShareApiAuthzFixture.Workspace}/{ShareApiAuthzFixture.ProjA}/logs");
		page.StatusCode.Should().Be(HttpStatusCode.OK, "the admin must be able to load ProjA's logs page");
		var html = await page.Content.ReadAsStringAsync();

		html.Should().Contain("share-modal", "the share modal must be on the page at all");
		html.Should().Contain("data-share-revoke",
			"the modal must offer a Revoke control — without it the only way to withdraw a minted link "
			+ "is a hand-written curl against the revoke endpoint");

		// The wiring. A Revoke button nothing listens to would satisfy a markup-only assertion while
		// changing nothing for the user, so the click handler and the request it issues are asserted
		// too — read from ts/logs.ts (the source the page's bundle is built from) rather than from the
		// built asset, because the bundle only exists after the Release-only BuildFrontend target has
		// run and this must mean the same thing in every configuration.
		var script = LogsScriptSource();
		script.Should().Contain("target.closest(\"[data-share-revoke]\")",
			"the document click handler must listen for the Revoke control — a querySelector for it "
			+ "elsewhere in the file is not a listener");
		script.Should().Contain("method: \"DELETE\"",
			"and it must issue the revoke as a DELETE, not merely render a button");
		script.Should().Contain("/api/share/${encodeURIComponent(link.token)}",
			"aimed at the revoke endpoint for the token the modal is showing");
	}

	// ts/logs.ts, read off the repo (the walk McpTestClientUsageTests uses). Reading the SOURCE keeps
	// this assertion configuration-independent: the wwwroot bundle is produced by the Release-only
	// BuildFrontend target, so a Debug run has nothing to fetch.
	static string LogsScriptSource()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir) && !Directory.Exists(Path.Combine(dir, "src", "PetBox.Web")))
			dir = Path.GetDirectoryName(dir);

		dir.Should().NotBeNullOrEmpty("the repo root (with src/PetBox.Web) must be findable from the test bin");
		return File.ReadAllText(Path.Combine(dir!, "src", "PetBox.Web", "ts", "logs.ts"));
	}

	// Cookie login as the fixture's admin (the same antiforgery scrape LogsSavedQueryAuthzTests does;
	// this client keeps its own cookies, so only the token has to be carried by hand).
	static async Task LoginAsync(HttpClient client)
	{
		using var loginPage = await client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);

		using var resp = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = "test123",
			["__RequestVerificationToken"] = loginHtml[valueStart..valueEnd],
		}));
		resp.StatusCode.Should().Be(HttpStatusCode.Redirect, "the admin login must succeed");
	}

	// Revoking twice must not error the second time in a way that leaks anything beyond "not found" —
	// the row is already gone, so this is the same DeleteAsync-returns-false path as an unknown token.
	[Fact]
	public async Task DeleteShare_AlreadyRevoked_SecondDeleteReturns404_NotAnError()
	{
		var token = await CreateShareAsync(ShareApiAuthzFixture.KeyA, ShareApiAuthzFixture.ProjA);
		using var first = await _client.SendAsync(DeleteReq(ShareApiAuthzFixture.KeyA, token, ShareApiAuthzFixture.ProjA));
		first.StatusCode.Should().Be(HttpStatusCode.OK);

		using var second = await _client.SendAsync(DeleteReq(ShareApiAuthzFixture.KeyA, token, ShareApiAuthzFixture.ProjA));
		second.StatusCode.Should().Be(HttpStatusCode.NotFound,
			"a second revoke of an already-revoked token is the same outcome as revoking an unknown one");
	}
}
