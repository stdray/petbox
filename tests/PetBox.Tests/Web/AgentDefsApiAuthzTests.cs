using System.Net;
using System.Text;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Web;

// agents:read / agents:write must gate /api/{project}/agent-defs. Fail CI if Authorize is removed.
// Mirrors ShareApiAuthzTests style: WebApplicationFactory + X-Api-Key.
public sealed class AgentDefsApiAuthzFixture : IAsyncLifetime
{
	public const string Proj = "adefauthz";
	public const string KeyRead = "yb_key_adef_read";
	public const string KeyWrite = "yb_key_adef_write";
	public const string KeyNone = "yb_key_adef_noscope";
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public AgentDefsApiAuthzFixture()
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
		await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = "$system", Name = "AdefAuthz" });
		await db.InsertAsync(new ApiKey
		{
			Key = KeyRead,
			ProjectKey = Proj,
			Scopes = ApiKeyScopes.AgentsRead,
			Name = "read",
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = KeyWrite,
			ProjectKey = Proj,
			Scopes = $"{ApiKeyScopes.AgentsRead},{ApiKeyScopes.AgentsWrite}",
			Name = "write",
			CreatedAt = DateTime.UtcNow,
		});
		await db.InsertAsync(new ApiKey
		{
			Key = KeyNone,
			ProjectKey = Proj,
			Scopes = "tasks:read",
			Name = "noscope",
			CreatedAt = DateTime.UtcNow,
		});
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

public sealed class AgentDefsApiAuthzTests : IClassFixture<AgentDefsApiAuthzFixture>
{
	readonly AgentDefsApiAuthzFixture _fx;
	readonly HttpClient _client;

	public AgentDefsApiAuthzTests(AgentDefsApiAuthzFixture fx)
	{
		_fx = fx;
		_client = fx.Client;
	}

	static HttpRequestMessage Req(HttpMethod method, string path, string apiKey, HttpContent? body = null)
	{
		var req = new HttpRequestMessage(method, path);
		req.Headers.Add("X-Api-Key", apiKey);
		if (body is not null) req.Content = body;
		return req;
	}

	[Fact]
	public async Task List_WithoutAgentsRead_Returns403()
	{
		using var resp = await _client.SendAsync(
			Req(HttpMethod.Get, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs", AgentDefsApiAuthzFixture.KeyNone));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"tasks:read must not authorize agents:read list");
	}

	[Fact]
	public async Task List_WithAgentsRead_Returns200()
	{
		using var resp = await _client.SendAsync(
			Req(HttpMethod.Get, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs", AgentDefsApiAuthzFixture.KeyRead));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task Put_WithoutAgentsWrite_Returns403()
	{
		var json = """
			{
			  "version": 0,
			  "definition": {
			    "name": "default",
			    "roles": [
			      { "slug": "worker", "tier": "worker", "requiredCapabilities": [] }
			    ]
			  }
			}
			""";
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var resp = await _client.SendAsync(
			Req(HttpMethod.Put, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs/default",
				AgentDefsApiAuthzFixture.KeyRead, content));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
			"agents:read alone must not authorize PUT (needs agents:write)");
	}

	[Fact]
	public async Task Put_WithAgentsWrite_Succeeds()
	{
		var json = """
			{
			  "version": 0,
			  "definition": {
			    "name": "default",
			    "roles": [
			      { "slug": "worker", "tier": "worker", "requiredCapabilities": [] }
			    ]
			  }
			}
			""";
		using var content = new StringContent(json, Encoding.UTF8, "application/json");
		using var resp = await _client.SendAsync(
			Req(HttpMethod.Put, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs/scoped",
				AgentDefsApiAuthzFixture.KeyWrite, content));
		resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
	}

	// End-to-end regression for the "server silently drops role.notes" defect: PUT a role's
	// free-text notes through the typed REST path, GET it back, and the prose must survive —
	// not just the wire shape, but the stored/canonical payload (a second PUT differing only
	// in notes must mint a new revision, not collapse to changed:false).
	[Fact]
	public async Task Put_RoleNotes_RoundTrip_And_NotesOnlyDiff_IsChanged()
	{
		static string Body(long version, string notes) => $$"""
			{
			  "version": {{version}},
			  "definition": {
			    "name": "default",
			    "roles": [
			      { "slug": "worker", "tier": "worker", "requiredCapabilities": [], "notes": "{{notes}}" }
			    ]
			  }
			}
			""";

		using var putContent = new StringContent(Body(0, "you are a LEAF, never spawn subagents"), Encoding.UTF8, "application/json");
		using var putResp = await _client.SendAsync(
			Req(HttpMethod.Put, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs/notesrt",
				AgentDefsApiAuthzFixture.KeyWrite, putContent));
		var putBody = await putResp.Content.ReadAsStringAsync();
		putResp.StatusCode.Should().Be(HttpStatusCode.OK, putBody);

		using var getResp = await _client.SendAsync(
			Req(HttpMethod.Get, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs/notesrt", AgentDefsApiAuthzFixture.KeyRead));
		var getBody = await getResp.Content.ReadAsStringAsync();
		getResp.StatusCode.Should().Be(HttpStatusCode.OK, getBody);
		getBody.Should().Contain("you are a LEAF, never spawn subagents",
			"the role's notes must survive the PUT -> GET round trip, not be silently dropped");

		using var ackDoc = System.Text.Json.JsonDocument.Parse(putBody);
		var version = ackDoc.RootElement.GetProperty("version").GetInt64();

		// A resubmit differing ONLY in notes must produce changed:true (notes are part of the
		// stored payload, not stripped before the same-payload comparison).
		using var putContent2 = new StringContent(Body(version, "a completely different briefing"), Encoding.UTF8, "application/json");
		using var putResp2 = await _client.SendAsync(
			Req(HttpMethod.Put, $"/api/{AgentDefsApiAuthzFixture.Proj}/agent-defs/notesrt",
				AgentDefsApiAuthzFixture.KeyWrite, putContent2));
		var putBody2 = await putResp2.Content.ReadAsStringAsync();
		putResp2.StatusCode.Should().Be(HttpStatusCode.OK, putBody2);
		using var ackDoc2 = System.Text.Json.JsonDocument.Parse(putBody2);
		ackDoc2.RootElement.GetProperty("changed").GetBoolean().Should()
			.BeTrue("two documents differing only in notes must not canonicalize to the same stored payload");
	}
}
