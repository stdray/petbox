using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// Integration tests for the legacy yobaconf-compatible bulk-resolve endpoint /v1/conf.
[Collection("WebAppFactory")]
public sealed class ConfV1Tests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string Ws = "wsv1";
	const string Project = "projv1";
	const string Key = "yb_key_v1conf_test";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public ConfV1Tests()
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
		MigrationRunner.Run(cs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.Workspaces.Where(w => w.Key == Ws).DeleteAsync();
		await db.Projects.Where(p => p.Key == Project).DeleteAsync();
		await db.ApiKeys.Where(k => k.Key == Key).DeleteAsync();

		await db.InsertAsync(new Workspace { Key = Ws, Name = "V1", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = Project, WorkspaceKey = Ws, Name = "V1" });
		await db.InsertAsync(new ApiKey { Key = Key, ProjectKey = Project, Scopes = "config:read", CreatedAt = DateTime.UtcNow });

		var configFactory = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
		var configDb = configFactory.GetConfigDb(Ws);
		// The per-workspace config DB is shared across test instances; clear it so repeated
		// InitializeAsync runs don't accumulate duplicate bindings (→ ambiguous → 409).
		await configDb.Bindings.DeleteAsync();
		var now = DateTime.UtcNow;
		await configDb.InsertAsync(new ConfigBinding { Path = "db.host", Value = "h1", Tags = $"ws:{Ws}", CreatedAt = now, UpdatedAt = now });
		await configDb.InsertAsync(new ConfigBinding { Path = "db.port", Value = "5432", Tags = $"ws:{Ws},env:dev", CreatedAt = now, UpdatedAt = now });
		await configDb.InsertAsync(new ConfigBinding { Path = "feature-x", Value = "true", Tags = $"ws:{Ws}", CreatedAt = now, UpdatedAt = now });
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", null);
	}

	static HttpRequestMessage Conf(string query, string headerName = "X-YobaConf-ApiKey")
	{
		var req = new HttpRequestMessage(HttpMethod.Get, "/v1/conf" + query);
		req.Headers.Add(headerName, Key);
		return req;
	}

	[Fact]
	public async Task Flat_ReturnsNestedTree()
	{
		using var resp = await _client.SendAsync(Conf("?env=dev"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("db").GetProperty("host").GetString().Should().Be("h1");
		doc.RootElement.GetProperty("db").GetProperty("port").GetString().Should().Be("5432");
		doc.RootElement.GetProperty("feature-x").GetString().Should().Be("true");
	}

	[Fact]
	public async Task TagFilter_ExcludesNonMatching()
	{
		// Without env:dev, db.port (tagged env:dev) must not resolve.
		using var resp = await _client.SendAsync(Conf(""));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("db").TryGetProperty("port", out _).Should().BeFalse();
		doc.RootElement.GetProperty("db").GetProperty("host").GetString().Should().Be("h1");
	}

	[Fact]
	public async Task EnvVarTemplate_ReturnsFlatUpperKeys()
	{
		using var resp = await _client.SendAsync(Conf("?env=dev&template=envvar"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("DB_HOST").GetString().Should().Be("h1");
		doc.RootElement.GetProperty("FEATURE_X").GetString().Should().Be("true");
	}

	[Fact]
	public async Task ETag_IfNoneMatch_Returns304()
	{
		using var first = await _client.SendAsync(Conf("?env=dev"));
		first.StatusCode.Should().Be(HttpStatusCode.OK);
		var etag = first.Headers.ETag!.Tag;

		var second = Conf("?env=dev");
		second.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
		using var resp = await _client.SendAsync(second);
		resp.StatusCode.Should().Be(HttpStatusCode.NotModified);
	}

	[Fact]
	public async Task NativeHeader_AlsoWorks()
	{
		using var resp = await _client.SendAsync(Conf("?env=dev", headerName: "X-Api-Key"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task MissingKey_Returns401()
	{
		using var resp = await _client.GetAsync("/v1/conf?env=dev");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}
}
