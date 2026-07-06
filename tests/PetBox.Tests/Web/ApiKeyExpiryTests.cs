using System.Net;
using System.Net.Http.Headers;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// Shared per-class host for ApiKeyExpiryTests (xUnit news the test class per test, so
// without this fixture every test boots its own WebApplicationFactory). No per-test reset
// is needed: the two keys are seeded once and every test only sends read requests. The
// class also left the serialized WebAppFactory collection: its per-class connection string
// moved from the process-global CONNECTIONSTRINGS__PETBOX env var to in-memory config, and
// no env var is written at all.
public sealed class ApiKeyExpiryFixture : IAsyncLifetime
{
	public const string ExpiredKey = "yb_key_expired_test";
	public const string ValidKey = "yb_key_valid_test";

	const string Ws = "wsexp";
	const string Project = "projexp";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public ApiKeyExpiryFixture()
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
						["Features:Config"] = "true",
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
		await db.Projects.Where(p => p.Key == Project).DeleteAsync();
		await db.ApiKeys.Where(k => k.Key == ExpiredKey || k.Key == ValidKey).DeleteAsync();

		await db.InsertAsync(new Workspace { Key = Ws, Name = "Exp", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = Project, WorkspaceKey = Ws, Name = "Exp" });
		await db.InsertAsync(new ApiKey { Key = ExpiredKey, ProjectKey = Project, Scopes = "config:read", CreatedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(-1) });
		await db.InsertAsync(new ApiKey { Key = ValidKey, ProjectKey = Project, Scopes = "config:read", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1) });
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

// WS6.1 — the auth handler rejects an ApiKey past its ExpiresAt.
public sealed class ApiKeyExpiryTests : IClassFixture<ApiKeyExpiryFixture>
{
	const string ExpiredKey = ApiKeyExpiryFixture.ExpiredKey;
	const string ValidKey = ApiKeyExpiryFixture.ValidKey;

	readonly HttpClient _client;

	public ApiKeyExpiryTests(ApiKeyExpiryFixture fx)
	{
		_client = fx.Client;
	}

	[Fact]
	public async Task ExpiredKey_Rejected()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Add("X-Api-Key", ExpiredKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task UnexpiredKey_Accepted()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Add("X-Api-Key", ValidKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	// `Authorization: Token <key>` is the form the mem0 Claude Code plugin sends; the auth
	// handler accepts it (token == the PetBox API key) so the plugin works against PetBox.
	[Fact]
	public async Task AuthorizationTokenHeader_Accepted()
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf");
		req.Headers.Authorization = new AuthenticationHeaderValue("Token", ValidKey);
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}
}
