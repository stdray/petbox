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
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;

namespace PetBox.Tests.Web;

// Integration tests for the deploy agent contract (/agent/*) and node onboarding
// (/api/deploy/nodes): scope enforcement, node-claim → node resolution, and key minting.
[Collection("WebAppFactory")]
public sealed class DeployApiTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	HttpClient _client = null!;

	const string AdminKey = "yb_key_deploy_admin_test";   // deploy:write
	const string NodeKey = "yb_key_deploy_node_test";     // agent:poll,agent:heartbeat
	readonly string _node = "node-" + Guid.NewGuid().ToString("N")[..8];

	public DeployApiTests()
	{
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b => b.UseEnvironment("Testing"));
	}

	public async Task InitializeAsync()
	{
		var cs = _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		_client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.InsertAsync(new ApiKey { Key = AdminKey, ProjectKey = "ops", Scopes = "deploy:read,deploy:write", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = NodeKey, ProjectKey = _node, Scopes = "agent:poll,agent:heartbeat", CreatedAt = DateTime.UtcNow });

		// deploy.db is shared across test instances (same temp dir) — clear it for isolation.
		var deploy = scope.ServiceProvider.GetRequiredService<DeployDb>();
		await deploy.Statuses.DeleteAsync();
		await deploy.Deployments.DeleteAsync();
		await deploy.Nodes.DeleteAsync();

		// A project + workspace + a config binding so poll can resolve env server-side.
		await db.Workspaces.Where(w => w.Key == "wsdep").DeleteAsync();
		await db.Projects.Where(p => p.Key == "proj").DeleteAsync();
		await db.InsertAsync(new Workspace { Key = "wsdep", Name = "Dep", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = "proj", WorkspaceKey = "wsdep", Name = "Proj" });
		var configDb = scope.ServiceProvider.GetRequiredService<IConfigDbFactory>().GetConfigDb("wsdep");
		await configDb.Bindings.DeleteAsync();
		var now = DateTime.UtcNow;
		await configDb.InsertAsync(new ConfigBinding { Path = "GREETING", Value = "hi", Tags = "ws:wsdep,project:proj", CreatedAt = now, UpdatedAt = now });

		var svc = scope.ServiceProvider.GetRequiredService<IDeployService>();
		await svc.UpsertNodeAsync(new NodeInput(_node, "Test node", "net.x", false));
		await svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", _node, "img1", DesiredState.Running, false, "net.x", "env:prod"));
	}

	public async Task DisposeAsync()
	{
		_client.Dispose();
		await _factory.DisposeAsync();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__PETBOX", null);
	}

	static HttpRequestMessage Req(HttpMethod m, string path, string key)
	{
		var r = new HttpRequestMessage(m, path);
		r.Headers.Add("X-Api-Key", key);
		return r;
	}

	[Fact]
	public async Task Poll_With_NodeKey_Returns_Assigned_Deployments()
	{
		using var resp = await _client.SendAsync(Req(HttpMethod.Get, "/agent/poll", NodeKey));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("nodeId").GetString().Should().Be(_node);
		var items = doc.RootElement.GetProperty("deployments");
		items.GetArrayLength().Should().Be(1);
		items[0].GetProperty("service").GetString().Should().Be("bot");
		items[0].GetProperty("project").GetString().Should().Be("proj");
	}

	[Fact]
	public async Task Poll_Resolves_Env_From_Project_Config()
	{
		using var resp = await _client.SendAsync(Req(HttpMethod.Get, "/agent/poll", NodeKey));
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		var env = doc.RootElement.GetProperty("deployments")[0].GetProperty("env");
		env.GetProperty("GREETING").GetString().Should().Be("hi");
	}

	[Fact]
	public async Task Poll_Without_AgentScope_Is_Forbidden()
	{
		// AdminKey has deploy:write but not agent:poll.
		using var resp = await _client.SendAsync(Req(HttpMethod.Get, "/agent/poll", AdminKey));
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}

	[Fact]
	public async Task Poll_Without_Key_Is_Unauthorized()
	{
		using var resp = await _client.GetAsync("/agent/poll");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task Heartbeat_With_NodeKey_Succeeds()
	{
		var req = Req(HttpMethod.Post, "/agent/heartbeat", NodeKey);
		req.Content = JsonContent.Create(new
		{
			actual = new[] { new { service = "bot", containerId = "c1", state = 2, imageDigest = "img1", healthy = true } },
		});
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task EnrollNode_With_DeployWrite_Mints_Working_NodeKey()
	{
		var newNode = "node-" + Guid.NewGuid().ToString("N")[..8];
		var req = Req(HttpMethod.Post, "/api/deploy/nodes", AdminKey);
		req.Content = JsonContent.Create(new { id = newNode, displayName = "Fresh", tags = "net.x", ephemeral = true, mintKey = true });
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		var minted = doc.RootElement.GetProperty("key").GetString();
		minted.Should().NotBeNullOrEmpty();
		doc.RootElement.GetProperty("node").GetProperty("id").GetString().Should().Be(newNode);

		// The minted key authenticates the agent poll for its node.
		using var poll = await _client.SendAsync(Req(HttpMethod.Get, "/agent/poll", minted!));
		poll.StatusCode.Should().Be(HttpStatusCode.OK);
	}

	[Fact]
	public async Task EnrollNode_Without_DeployWrite_Is_Forbidden()
	{
		var req = Req(HttpMethod.Post, "/api/deploy/nodes", NodeKey);   // node key lacks deploy:write
		req.Content = JsonContent.Create(new { id = "nope", mintKey = false });
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
	}
}
