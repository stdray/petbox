using System.Net;
using System.Net.Http.Json;
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

// authz-cleanup-phase2-rest: ConfigApi.Create/Delete (POST/DELETE /api/config/{workspaceKey}/bindings)
// gated on nothing but the "ConfigWrite" ScopeRequirement policy (config:write scope present — NOT
// which workspace the caller's project belongs to) plus a self-referential, attacker-controlled
// `dto.Tags.Contains("ws:{workspaceKey}")` string check. Any config:write key from any project could
// write/soft-delete secret bindings in ANY workspace. Fixed by deriving the caller's real workspace
// from their project claim (Project.WorkspaceKey — the same derivation Conf() already does for reads)
// and requiring it to match the route's {workspaceKey}. These tests drive the REST endpoints directly
// with X-Api-Key, mirroring ConfV1Tests/ConfigDeleteBindingTests' WebApplicationFactory style.
public sealed class ConfigApiWorkspaceAuthzFixture : IAsyncLifetime
{
	public const string WsA = "authzwsa";
	public const string WsB = "authzwsb";
	public const string ProjA = "authzproja";
	public const string ProjB = "authzprojb";
	public const string KeyA = "yb_key_authz_config_a";
	public const string KeyB = "yb_key_authz_config_b";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	// A binding pre-seeded in wsB, targeted by the cross-workspace delete-denial test.
	public long WsBBindingId { get; private set; }

	public ConfigApiWorkspaceAuthzFixture()
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
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.InsertAsync(new Workspace { Key = WsA, Name = "WsA", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Workspace { Key = WsB, Name = "WsB", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = WsA, Name = "ProjA" });
		await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = WsB, Name = "ProjB" });
		await db.InsertAsync(new ApiKey { Key = KeyA, ProjectKey = ProjA, Scopes = "config:write", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = KeyB, ProjectKey = ProjB, Scopes = "config:write", CreatedAt = DateTime.UtcNow });

		var configFactory = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
		var configDb = configFactory.GetConfigDb(WsB);
		var now = DateTime.UtcNow;
		WsBBindingId = Convert.ToInt64(await configDb.InsertWithIdentityAsync(new ConfigBinding
		{
			Path = "wsb.secret",
			Value = "v",
			Tags = $"ws:{WsB}",
			CreatedAt = now,
			UpdatedAt = now,
		}));
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class ConfigApiWorkspaceAuthzTests : IClassFixture<ConfigApiWorkspaceAuthzFixture>
{
	readonly ConfigApiWorkspaceAuthzFixture _fx;
	readonly HttpClient _client;

	public ConfigApiWorkspaceAuthzTests(ConfigApiWorkspaceAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static HttpRequestMessage CreateReq(string apiKey, string workspaceKey, string path, string tags)
	{
		var req = new HttpRequestMessage(HttpMethod.Post, $"/api/config/{workspaceKey}/bindings");
		req.Headers.Add("X-Api-Key", apiKey);
		req.Content = JsonContent.Create(new { path, value = "v", tags });
		return req;
	}

	static HttpRequestMessage DeleteReq(string apiKey, string workspaceKey, string path, string tags)
	{
		var req = new HttpRequestMessage(
			HttpMethod.Delete,
			$"/api/config/{workspaceKey}/bindings?path={Uri.EscapeDataString(path)}&tags={Uri.EscapeDataString(tags)}");
		req.Headers.Add("X-Api-Key", apiKey);
		return req;
	}

	[Fact]
	public async Task Create_OwnWorkspace_Succeeds()
	{
		var path = $"own.path.{Guid.NewGuid():N}";
		using var resp = await _client.SendAsync(CreateReq(
			ConfigApiWorkspaceAuthzFixture.KeyA, ConfigApiWorkspaceAuthzFixture.WsA, path, $"ws:{ConfigApiWorkspaceAuthzFixture.WsA}"));
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"a config:write key must be able to create a binding in its OWN project's workspace");
	}

	[Fact]
	public async Task Create_ForeignWorkspace_Returns403_AndDoesNotCreate()
	{
		var path = $"attack.path.{Guid.NewGuid():N}";
		// KeyA belongs to project A, whose workspace is WsA — targeting WsB's bindings route
		// must be denied even though the key genuinely carries config:write.
		using var resp = await _client.SendAsync(CreateReq(
			ConfigApiWorkspaceAuthzFixture.KeyA, ConfigApiWorkspaceAuthzFixture.WsB, path, $"ws:{ConfigApiWorkspaceAuthzFixture.WsB}"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a config:write key authorized for project A's workspace (WsA) must not write into a foreign workspace (WsB)");

		using var scope = _fx.Factory.Services.CreateScope();
		var configFactory = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
		var configDb = configFactory.GetConfigDb(ConfigApiWorkspaceAuthzFixture.WsB);
		configDb.Bindings.Any(b => b.Path == path).Should().BeFalse("the cross-workspace write must not land");
	}

	[Fact]
	public async Task Delete_OwnWorkspace_Succeeds()
	{
		// Seed a binding via the authorized own-workspace Create call, then delete it with the
		// same key — end-to-end through the REST surface, not a direct DB poke.
		var path = $"own.delete.{Guid.NewGuid():N}";
		var tags = $"ws:{ConfigApiWorkspaceAuthzFixture.WsA}";
		using (var createResp = await _client.SendAsync(CreateReq(
			ConfigApiWorkspaceAuthzFixture.KeyA, ConfigApiWorkspaceAuthzFixture.WsA, path, tags)))
		{
			createResp.StatusCode.Should().Be(HttpStatusCode.OK);
		}

		using var delResp = await _client.SendAsync(DeleteReq(
			ConfigApiWorkspaceAuthzFixture.KeyA, ConfigApiWorkspaceAuthzFixture.WsA, path, tags));
		delResp.StatusCode.Should().Be(HttpStatusCode.OK,
			"a config:write key must be able to delete a binding in its OWN project's workspace");
		using var doc = JsonDocument.Parse(await delResp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Delete_ForeignWorkspace_Returns403_AndDoesNotDelete()
	{
		// KeyA (project A / WsA) attacks the pre-seeded WsB binding by targeting WsB's route.
		using var resp = await _client.SendAsync(DeleteReq(
			ConfigApiWorkspaceAuthzFixture.KeyA, ConfigApiWorkspaceAuthzFixture.WsB, "wsb.secret", $"ws:{ConfigApiWorkspaceAuthzFixture.WsB}"));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"a config:write key authorized for WsA must not soft-delete a binding in foreign workspace WsB");

		using var scope = _fx.Factory.Services.CreateScope();
		var configFactory = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>();
		var configDb = configFactory.GetConfigDb(ConfigApiWorkspaceAuthzFixture.WsB);
		var row = configDb.Bindings.First(b => b.Id == _fx.WsBBindingId);
		row.IsDeleted.Should().BeFalse("the cross-workspace delete must not have soft-deleted the victim binding");
	}
}
