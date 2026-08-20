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
