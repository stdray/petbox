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

// WS6.1 — the auth handler rejects an ApiKey past its ExpiresAt.
[Collection("WebAppFactory")]
public sealed class ApiKeyExpiryTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string Ws = "wsexp";
	const string Project = "projexp";
	const string ExpiredKey = "yb_key_expired_test";
	const string ValidKey = "yb_key_valid_test";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public ApiKeyExpiryTests()
	{
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["Features:Config"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
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
		_client.Dispose();
		await _factory.DisposeAsync();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", null);
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
