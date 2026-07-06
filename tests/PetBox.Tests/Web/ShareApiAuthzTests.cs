using System.Net;
using System.Net.Http.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

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
						["Features:Logging"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = "$system", Name = "ProjA" });
		await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = "$system", Name = "ProjB" });
		await db.InsertAsync(new ApiKey { Key = KeyA, ProjectKey = ProjA, Scopes = "logs:query", CreatedAt = DateTime.UtcNow });
	}

	public async Task DisposeAsync()
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

	static HttpRequestMessage ShareReq(string apiKey, string projectKey)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, "/api/share");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = JsonContent.Create(new
		{
			projectKey,
			kql = "events | take 10",
			ttlMinutes = 60,
			columns = (string[]?)null,
			modes = (Dictionary<string, int>?)null,
			logName = (string?)null,
		});
		return req;
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
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		db.ShareLinks.Any(s => s.ProjectKey == ShareApiAuthzFixture.ProjB).Should().BeFalse(
			"no share token must have been minted for the foreign project");
	}
}
