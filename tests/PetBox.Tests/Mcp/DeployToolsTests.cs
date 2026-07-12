using System.Security.Claims;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;
using PetBox.Deploy.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Exercises the deploy.* MCP tool methods directly (mocked HttpContext + real DeployDb +
// PetBoxDb for key minting). Validates tool logic, scope guards, and key minting.
public sealed class DeployToolsTests : IDisposable
{
	readonly string _dir;
	readonly DeployService _svc;
	readonly PetBoxDb _db;

	public DeployToolsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-deploytools-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var deployCs = $"Data Source={Path.Combine(_dir, "deploy.db")};Cache=Shared";
		DeploySchema.Ensure(deployCs);
		// The service owns its connections now (one per call, via the factory) — there is no
		// long-lived DeployDb for the fixture to hold.
		_svc = new DeployService(new DeployDbFactory(deployCs));

		var coreCs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(coreCs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(coreCs));
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task NodeUpsert_Mints_Key_And_NodeList_Shows_It()
	{
		var r = Json(await DeployTools.NodeUpsertAsync(Http("deploy:write"), Flags(), _svc, _db,
			"vdsina-1", "VDSina", "net.x", ephemeral: false, mintKey: true));
		r.GetProperty("key").GetString().Should().StartWith("yb_key_node_");
		r.GetProperty("node").GetProperty("id").GetString().Should().Be("vdsina-1");

		// minted key persisted with the node-agent scopes, project = node id
		var minted = _db.ApiKeys.Where(k => k.ProjectKey == "vdsina-1").ToList();
		minted.Should().ContainSingle();
		minted[0].Scopes.Should().Contain("agent:poll").And.Contain("agent:heartbeat");

		var list = Json(await DeployTools.NodeListAsync(Http("deploy:read"), Flags(), _svc));
		list.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString())
			.Should().Contain("vdsina-1");
	}

	[Fact]
	public async Task Upsert_Then_Stop_Start_Move()
	{
		await DeployTools.NodeUpsertAsync(Http("deploy:write"), Flags(), _svc, _db, "n1");
		await DeployTools.NodeUpsertAsync(Http("deploy:write"), Flags(), _svc, _db, "n2");

		var created = Json(await DeployTools.UpsertAsync(Http("deploy:write"), Flags(), _svc,
			"bot", "proj", "n1", "img1"));
		var id = created.GetProperty("deployment").GetProperty("id").GetString()!;
		created.GetProperty("deployment").GetProperty("service").GetString().Should().Be("bot");

		await DeployTools.StopAsync(Http("deploy:write"), Flags(), _svc, id);
		(await _svc.GetDeploymentAsync(id))!.DesiredState.Should().Be(DesiredState.Stopped);

		await DeployTools.StartAsync(Http("deploy:write"), Flags(), _svc, id);
		(await _svc.GetDeploymentAsync(id))!.DesiredState.Should().Be(DesiredState.Running);

		await DeployTools.MoveAsync(Http("deploy:write"), Flags(), _svc, id, "n2");
		(await _svc.GetDeploymentAsync(id))!.NodeId.Should().Be("n2");

		var del = Json(await DeployTools.DeleteAsync(Http("deploy:write"), Flags(), _svc, id));
		del.GetProperty("deleted").GetBoolean().Should().BeTrue();
		(await _svc.ListDeploymentsAsync()).Should().BeEmpty();
	}

	[Fact]
	public async Task Upsert_With_RunSpec_Then_Stop_Preserves_It()
	{
		await DeployTools.NodeUpsertAsync(Http("deploy:write"), Flags(), _svc, _db, "n1");

		var created = Json(await DeployTools.UpsertAsync(Http("deploy:write"), Flags(), _svc,
			"web", "proj", "n1", "img1",
			ports: ["127.0.0.1:8080:8080"],
			volumes: ["/opt/web/logs:/app/logs"],
			restart: "always",
			healthcheckCmd: "curl -f http://localhost:8080/health",
			healthcheckInterval: "30s",
			memory: "256m",
			labels: ["team=infra"]));
		var dep = created.GetProperty("deployment");
		dep.GetProperty("runSpec").GetProperty("ports")[0].GetString().Should().Be("127.0.0.1:8080:8080");
		dep.GetProperty("runSpec").GetProperty("restart").GetString().Should().Be("always");
		var id = dep.GetProperty("id").GetString()!;
		var hashBefore = dep.GetProperty("configHash").GetString();

		// stop goes through ToInput — the run-spec must survive the round-trip
		await DeployTools.StopAsync(Http("deploy:write"), Flags(), _svc, id);
		var after = (await _svc.GetDeploymentAsync(id))!;
		after.RunSpec.Ports.Should().Equal("127.0.0.1:8080:8080");
		after.RunSpec.Volumes.Should().Equal("/opt/web/logs:/app/logs");
		after.RunSpec.Healthcheck!.Interval.Should().Be("30s");
		after.RunSpec.Resources!.Memory.Should().Be("256m");
		after.RunSpec.Labels!["team"].Should().Be("infra");
		after.ConfigHash.Should().NotBe(hashBefore);   // desired state changed → hash changed
	}

	[Fact]
	public async Task Upsert_With_Domain_Creates_Site_RunSpec()
	{
		await DeployTools.NodeUpsertAsync(Http("deploy:write"), Flags(), _svc, _db, "n1");
		var created = Json(await DeployTools.UpsertAsync(Http("deploy:write"), Flags(), _svc,
			"web", "proj", "n1", "img1",
			ports: ["127.0.0.1:8080:8080"],
			domain: "app.example.com"));
		var site = created.GetProperty("deployment").GetProperty("runSpec").GetProperty("site");
		site.GetProperty("domain").GetString().Should().Be("app.example.com");
		site.GetProperty("port").GetInt32().Should().Be(8080);   // derived from ports[0]
	}

	[Fact]
	public async Task Upsert_With_Bad_RunSpec_Throws()
	{
		await DeployTools.NodeUpsertAsync(Http("deploy:write"), Flags(), _svc, _db, "n1");
		var ex = await Assert.ThrowsAsync<ArgumentException>(() => DeployTools.UpsertAsync(Http("deploy:write"), Flags(), _svc,
			"web", "proj", "n1", "img1", ports: ["oops"]));
		ex.Message.Should().Contain("port");
	}

	[Fact]
	public async Task Write_Tool_With_Only_ReadScope_Throws()
	{
		// Tools throw on the scope assert; McpErrorEnvelopeFilter renders {error} on the wire.
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			DeployTools.NodeUpsertAsync(Http("deploy:read"), Flags(), _svc, _db, "x"));
	}

	[Fact]
	public async Task List_Without_DeployScope_Throws()
	{
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			DeployTools.NodeListAsync(Http("tasks:read"), Flags(), _svc));
	}

	static IHttpContextAccessor Http(string scopes) =>
		new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext
			{
				User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("project", "ops"), new Claim("scopes", scopes)], "test")),
				RequestServices = TestProjectCatalog.Services,
			},
		};

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Deploy"] = "true" }).Build());

	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
