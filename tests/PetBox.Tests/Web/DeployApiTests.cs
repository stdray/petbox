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

// Shared per-class host for DeployApiTests (xUnit news the test class per test, so without
// this fixture every test boots its own WebApplicationFactory). No per-test reset is
// needed: the node + deployment are seeded once, tests only read them or enroll fresh
// Guid-named nodes; the heartbeat status write is invisible to the poll assertions.
// The class also left the serialized WebAppFactory collection: its per-class connection
// string moved from the process-global CONNECTIONSTRINGS__PETBOX env var (which would leak
// into concurrently booting hosts) to in-memory config, and no env var is written at all.
public sealed class DeployApiFixture : IAsyncLifetime
{
	public const string AdminKey = "yb_key_deploy_admin_test";   // deploy:write
	public const string NodeKey = "yb_key_deploy_node_test";     // agent:poll,agent:heartbeat, bound to Node
																 // A key that is NOT a node key but whose PROJECT is named exactly like the node, and which
																 // carries the agent scopes so nothing but the carrier can be what separates it from NodeKey.
																 // Pre-M050 this key and NodeKey were byte-identical to /agent/* — same value, same claim.
	public const string TwinProjectKey = "yb_key_deploy_twin_project_test";

	public string Node { get; } = "node-" + Guid.NewGuid().ToString("N")[..8];
	WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;
	// Exposed so a test can assert the SHAPE of a minted key row, not only its behaviour.
	public IServiceProvider Services => Factory.Services;

	public DeployApiFixture()
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
		await db.InsertAsync(new ApiKey { Key = AdminKey, ProjectKey = "ops", Scopes = "deploy:read,deploy:write", CreatedAt = DateTime.UtcNow });
		// The node key carries its host in HostId and NO project — the shape M050 introduced and the
		// enroll path now mints.
		await db.InsertAsync(new ApiKey { Key = NodeKey, HostId = Node, ProjectKey = "", Scopes = "agent:poll,agent:heartbeat", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new ApiKey { Key = TwinProjectKey, ProjectKey = Node, Scopes = "agent:poll,agent:heartbeat", CreatedAt = DateTime.UtcNow });

		// deploy.db is shared across test instances (same temp dir) — clear it for isolation.
		// DeployDb is no longer in DI (only IDeployDbFactory is) — open a connection and own it.
		await using (var deploy = scope.ServiceProvider.GetRequiredService<IDeployDbFactory>().Open())
		{
			await deploy.Statuses.DeleteAsync();
			await deploy.Deployments.DeleteAsync();
			await deploy.Nodes.DeleteAsync();
		}

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
		await svc.UpsertNodeAsync(new NodeInput(Node, "Test node", "net.x", false));
		await svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", Node, "img1", DesiredState.Running, false, "net.x", "env:prod"));
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
	}
}

// Integration tests for the deploy agent contract (/agent/*) and node onboarding
// (/api/deploy/nodes): scope enforcement, node-claim → node resolution, and key minting.
public sealed class DeployApiTests : IClassFixture<DeployApiFixture>
{
	const string AdminKey = DeployApiFixture.AdminKey;
	const string NodeKey = DeployApiFixture.NodeKey;
	const string TwinProjectKey = DeployApiFixture.TwinProjectKey;

	readonly HttpClient _client;
	readonly string _node;
	readonly IServiceProvider _services;

	public DeployApiTests(DeployApiFixture fx)
	{
		_client = fx.Client;
		_node = fx.Node;
		_services = fx.Services;
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

	// THE CLOSED COLLISION (spec node-grant-own-carrier).
	//
	// TwinProjectKey is a PROJECT key whose ProjectKey is the node's id, and it carries agent:poll —
	// so scope cannot be what refuses it. Pre-M050 /agent/poll read the node id out of the `project`
	// claim, which means this key and the real node key presented the deploy plane with the SAME
	// string in the SAME claim: this request would have succeeded and returned the node's
	// deployments to a caller that is not that node. It is refused now because the node axis lives
	// in a different claim, not because the two are spelled differently — no rename could have
	// produced this refusal, and no rename can undo it.
	[Fact]
	public async Task Poll_With_ProjectKey_Named_Like_The_Node_Does_Not_Resolve_As_That_Node()
	{
		using var resp = await _client.SendAsync(Req(HttpMethod.Get, "/agent/poll", TwinProjectKey));

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("host claim");

		// And the control: the same request with the real node key — same node id, different
		// carrier — does resolve. The two differ in nothing but which column the id lives in.
		using var ok = await _client.SendAsync(Req(HttpMethod.Get, "/agent/poll", NodeKey));
		ok.StatusCode.Should().Be(HttpStatusCode.OK);
		using var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
		doc.RootElement.GetProperty("nodeId").GetString().Should().Be(_node);
	}

	// Same closure on the write half of the agent contract: heartbeat is what stamps a node online,
	// so a project key resolving as a node here would let a stranger keep a dead node looking alive.
	[Fact]
	public async Task Heartbeat_With_ProjectKey_Named_Like_The_Node_Is_Refused()
	{
		var req = Req(HttpMethod.Post, "/agent/heartbeat", TwinProjectKey);
		req.Content = JsonContent.Create(new { actual = Array.Empty<object>() });
		using var resp = await _client.SendAsync(req);

		resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("host claim");
	}

	// The minted key's SHAPE, not just its behaviour: the enroll path must put the node id in HostId
	// and leave ProjectKey empty. Asserting the row is what stops a later "harmless" revert to
	// ProjectKey = node.Id from passing the behavioural tests above by accident.
	[Fact]
	public async Task EnrollNode_Mints_A_Key_Carrying_HostId_And_No_ProjectKey()
	{
		var newNode = "node-" + Guid.NewGuid().ToString("N")[..8];
		var req = Req(HttpMethod.Post, "/api/deploy/nodes", AdminKey);
		req.Content = JsonContent.Create(new { id = newNode, mintKey = true });
		using var resp = await _client.SendAsync(req);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
		var minted = doc.RootElement.GetProperty("key").GetString()!;

		using var scope = _services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		var row = db.ApiKeys.First(k => k.Key == minted);

		row.HostId.Should().Be(newNode);
		row.ProjectKey.Should().BeEmpty();
	}
}
