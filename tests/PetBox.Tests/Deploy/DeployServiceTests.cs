using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Deploy.Contract;
using PetBox.Deploy.Data;
using PetBox.Deploy.Services;

namespace PetBox.Tests.Deploy;

// Covers the fleet-wide deploy service CRUD that the agent endpoints, MCP tools and UI
// all delegate to: node registry, per-(service,node) desired state, the one-copy-per-node
// invariant, ConfigHash, and node-delete cascade.
[Collection("DataModule")]
public sealed class DeployServiceTests : IDisposable
{
	readonly string _dir;
	readonly DeployDb _db;
	readonly DeployService _svc;

	public DeployServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-deploysvc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "deploy.db")};Cache=Shared";
		DeploySchema.Ensure(cs);
		_db = new DeployDb(DeployDb.CreateOptions(cs));
		_svc = new DeployService(_db);
	}

	public void Dispose()
	{
		_db.Dispose();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task UpsertNode_Then_Get_And_List()
	{
		var v = await _svc.UpsertNodeAsync(new NodeInput("VDSina-1", "VDSina", "net.x, disk=nvme", Ephemeral: false));
		v.Id.Should().Be("vdsina-1");                  // normalized lowercase
		v.Tags.Should().Be("net.x,disk=nvme");          // trimmed CSV
		v.Online.Should().BeFalse();                     // never reported
		v.Deployments.Should().Be(0);

		(await _svc.GetNodeAsync("vdsina-1"))!.DisplayName.Should().Be("VDSina");
		(await _svc.ListNodesAsync()).Select(n => n.Id).Should().Equal("vdsina-1");
	}

	[Fact]
	public async Task UpsertNode_Preserves_CreatedAt_On_Update()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "First", "", false));
		var stored = (await _svc.GetNodeAsync("n1"))!;          // DB-precision CreatedAt
		var second = await _svc.UpsertNodeAsync(new NodeInput("n1", "Renamed", "net.kinopub", false));
		second.CreatedAt.Should().Be(stored.CreatedAt);         // preserved, not re-stamped
		second.DisplayName.Should().Be("Renamed");
	}

	[Fact]
	public async Task UpsertDeployment_Computes_ConfigHash_And_Lists_With_Null_Status()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "net.x", false));
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(
			Id: null, Service: "Bot", Project: "proj", NodeId: "n1", ImageDigest: "sha256:abc",
			DesiredState: DesiredState.Running, Relocatable: true, RequiredTags: "net.x", ConfigTags: "env:prod"));

		d.Id.Should().NotBeNullOrEmpty();
		d.Service.Should().Be("bot");
		d.Project.Should().Be("proj");
		d.ConfigHash.Should().HaveLength(64);            // sha256 hex
		d.ActualState.Should().BeNull();                 // no heartbeat yet
		(await _svc.ListNodesAsync()).Single().Deployments.Should().Be(1);
	}

	[Fact]
	public void ConfigHash_Changes_With_Image_And_Is_Stable()
	{
		var h1 = DeployService.ComputeConfigHash("sha256:a", "env:prod", DesiredState.Running, "proj");
		var h1Again = DeployService.ComputeConfigHash("sha256:a", "env:prod", DesiredState.Running, "proj");
		var h2 = DeployService.ComputeConfigHash("sha256:b", "env:prod", DesiredState.Running, "proj");
		h1.Should().Be(h1Again);
		h1.Should().NotBe(h2);
	}

	[Fact]
	public async Task Upsert_Stores_RunSpec_And_Poll_Carries_It()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		var spec = new RunSpec(
			Ports: ["127.0.0.1:8080:8080"],
			Volumes: ["/opt/app/logs:/app/logs", "/opt/app/keys:/app/keys:ro"],
			Restart: "unless-stopped",
			Healthcheck: new HealthcheckSpec("curl -f http://localhost:8080/health", "30s", "5s", 3),
			Resources: new ResourcesSpec("256m", 0.5),
			Network: "bridge",
			Command: ["python", "-m", "bot"],
			Labels: new Dictionary<string, string> { ["team"] = "infra" });
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(
			null, "bot", "proj", "n1", "img1", DesiredState.Running, false, "", "", spec));

		d.RunSpec.Ports.Should().Equal("127.0.0.1:8080:8080");
		d.RunSpec.Volumes.Should().HaveCount(2);
		d.RunSpec.Healthcheck!.Retries.Should().Be(3);
		d.RunSpec.Resources!.Memory.Should().Be("256m");
		d.RunSpec.Labels!["team"].Should().Be("infra");

		var poll = await _svc.PollAsync("n1");
		poll.Deployments[0].RunSpec!.Ports.Should().Equal("127.0.0.1:8080:8080");
		poll.Deployments[0].RunSpec!.Command.Should().Equal("python", "-m", "bot");
	}

	[Fact]
	public void ConfigHash_Is_Sensitive_To_Every_RunSpec_Field()
	{
		static string Hash(RunSpec? s) =>
			DeployService.ComputeConfigHash("img", "env:prod", DesiredState.Running, "proj", RunSpecJson.ToCanonicalJson(s));

		string[] variants =
		[
			Hash(null),
			Hash(new RunSpec(Ports: ["8080:8080"])),
			Hash(new RunSpec(Volumes: ["/a:/b"])),
			Hash(new RunSpec(Restart: "always")),
			Hash(new RunSpec(Healthcheck: new HealthcheckSpec("true"))),
			Hash(new RunSpec(Resources: new ResourcesSpec("256m"))),
			Hash(new RunSpec(Network: "host")),
			Hash(new RunSpec(Command: ["run"])),
			Hash(new RunSpec(Labels: new Dictionary<string, string> { ["k"] = "v" })),
		];
		variants.Should().OnlyHaveUniqueItems();
		Hash(new RunSpec(Ports: ["8080:8080"])).Should().Be(variants[1]);   // and stable
	}

	[Fact]
	public void Canonical_RunSpec_Json_Is_Stable_And_Empty_For_Empty_Spec()
	{
		RunSpecJson.ToCanonicalJson(null).Should().Be("{}");
		// effectively-empty collapses to "{}" too
		RunSpecJson.ToCanonicalJson(new RunSpec(Ports: [], Labels: new Dictionary<string, string>())).Should().Be("{}");
		// label insertion order does not change the canonical form (sorted on write)
		var a = RunSpecJson.ToCanonicalJson(new RunSpec(Labels: new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }));
		var b = RunSpecJson.ToCanonicalJson(new RunSpec(Labels: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }));
		a.Should().Be(b);
	}

	[Fact]
	public async Task Site_Spec_Derives_Port_From_First_Ports_Entry_And_Validates()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));

		// port omitted → derived from the host port of the first ports entry
		var derived = await _svc.UpsertDeploymentAsync(new DeploymentInput(
			null, "web", "proj", "n1", "img1", DesiredState.Running, false, "", "",
			new RunSpec(Ports: ["127.0.0.1:8080:80"], Site: new SiteSpec("App.Example.COM"))));
		derived.RunSpec.Site!.Domain.Should().Be("app.example.com");   // lowercased
		derived.RunSpec.Site!.Port.Should().Be(8080);

		// explicit port wins
		var explicitPort = await _svc.UpsertDeploymentAsync(new DeploymentInput(
			derived.Id, "web", "proj", "n1", "img1", DesiredState.Running, false, "", "",
			new RunSpec(Ports: ["127.0.0.1:8080:80"], Site: new SiteSpec("app.example.com", 9090))));
		explicitPort.RunSpec.Site!.Port.Should().Be(9090);
		explicitPort.ConfigHash.Should().NotBe(derived.ConfigHash);    // route is hashed

		// no ports and no explicit port → rejected; bad domain → rejected
		Func<RunSpec, Func<Task>> act = s => () => _svc.UpsertDeploymentAsync(new DeploymentInput(
			null, "web2", "proj", "n1", "img1", DesiredState.Running, false, "", "", s));
		await act(new RunSpec(Site: new SiteSpec("app.example.com"))).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Ports: ["8080:80"], Site: new SiteSpec("not a domain"))).Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task Heartbeat_Stores_HostReport_And_View_Computes_Warnings()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));

		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport([], Host: new HostReport(
			Security: new HostSecurity(RootLoginEnabled: true, PasswordAuthEnabled: false),
			Memory: new HostMemory(TotalMb: 1000, AvailableMb: 50),
			Disk: new HostDisk(TotalGb: 40, FreeGb: 25),
			Os: "Ubuntu 24.04.1 LTS")));

		var view = (await _svc.GetNodeAsync("n1"))!;
		view.Host!.Os.Should().Be("Ubuntu 24.04.1 LTS");
		view.Host.Memory!.AvailableMb.Should().Be(50);
		view.Warnings.Should().BeEquivalentTo(
			"root SSH login is not disabled",
			"low memory: 50 MB available of 1000 MB");

		// hardened + healthy snapshot clears the warnings; legacy heartbeat (null host) keeps the report
		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport([], Host: new HostReport(
			Security: new HostSecurity(false, false),
			Memory: new HostMemory(1000, 600),
			Disk: new HostDisk(40, 25))));
		(await _svc.GetNodeAsync("n1"))!.Warnings.Should().BeNull();

		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport([]));
		(await _svc.GetNodeAsync("n1"))!.Host.Should().NotBeNull();
	}

	[Fact]
	public async Task Node_ReUpsert_Preserves_AgentReported_Capabilities_And_HostReport()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport([],
			Capabilities: ["docker", "caddy"],
			Host: new HostReport(Os: "Ubuntu 24.04")));

		// operator re-enroll / edit must not wipe what the agent reported
		var after = await _svc.UpsertNodeAsync(new NodeInput("n1", "Renamed", "net.x", false));
		after.Capabilities.Should().Be("docker,caddy");
		after.Host!.Os.Should().Be("Ubuntu 24.04");
	}

	[Fact]
	public void ComputeWarnings_Thresholds()
	{
		DeployService.ComputeWarnings(null).Should().BeEmpty();
		DeployService.ComputeWarnings(new HostReport()).Should().BeEmpty();

		// password auth allowed
		DeployService.ComputeWarnings(new HostReport(Security: new HostSecurity(PasswordAuthEnabled: true)))
			.Should().ContainSingle(w => w.Contains("password SSH auth"));

		// relative floor: 9% of total
		DeployService.ComputeWarnings(new HostReport(Memory: new HostMemory(10000, 900)))
			.Should().ContainSingle(w => w.StartsWith("low memory"));
		// absolute floor: < 150 MB even when total is small
		DeployService.ComputeWarnings(new HostReport(Memory: new HostMemory(1000, 120)))
			.Should().ContainSingle(w => w.StartsWith("low memory"));
		// healthy: 50%
		DeployService.ComputeWarnings(new HostReport(Memory: new HostMemory(1000, 500))).Should().BeEmpty();

		// disk floors
		DeployService.ComputeWarnings(new HostReport(Disk: new HostDisk(100, 5)))
			.Should().ContainSingle(w => w.StartsWith("low disk"));
		DeployService.ComputeWarnings(new HostReport(Disk: new HostDisk(10, 1.5)))
			.Should().ContainSingle(w => w.StartsWith("low disk"));
		DeployService.ComputeWarnings(new HostReport(Disk: new HostDisk(40, 25))).Should().BeEmpty();
	}

	[Fact]
	public async Task Heartbeat_Stores_Capabilities_And_PerService_Error()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "web", "proj", "n1", "img1", DesiredState.Running, false, "", ""));

		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport(
			[new ActualReport("web", null, ActualState.Missing, null, Healthy: false, Error: "site route not applied: caddy is not available on this node")],
			Capabilities: ["docker"]));

		(await _svc.GetNodeAsync("n1"))!.Capabilities.Should().Be("docker");
		var view = (await _svc.GetDeploymentAsync(d.Id))!;
		view.Error.Should().Contain("caddy is not available");
		view.ActualState.Should().Be(ActualState.Missing);

		// error clears on a clean report; legacy heartbeat (null capabilities) keeps the last value
		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport(
			[new ActualReport("web", "c1", ActualState.Running, "img1", Healthy: true)]));
		(await _svc.GetDeploymentAsync(d.Id))!.Error.Should().BeNull();
		(await _svc.GetNodeAsync("n1"))!.Capabilities.Should().Be("docker");
	}

	[Fact]
	public async Task Upsert_Rejects_Invalid_RunSpec_Fields()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		Func<RunSpec, Func<Task>> act = s => () => _svc.UpsertDeploymentAsync(new DeploymentInput(
			null, "bot", "proj", "n1", "img1", DesiredState.Running, false, "", "", s));

		await act(new RunSpec(Ports: ["oops"])).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Volumes: ["relative:/x"])).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Restart: "sometimes")).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Healthcheck: new HealthcheckSpec(" "))).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Healthcheck: new HealthcheckSpec("true", Interval: "soon"))).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Resources: new ResourcesSpec("lots"))).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Resources: new ResourcesSpec(null, -1))).Should().ThrowAsync<ArgumentException>();
		await act(new RunSpec(Labels: new Dictionary<string, string> { ["petbox.service"] = "x" })).Should().ThrowAsync<ArgumentException>();
	}

	[Fact]
	public async Task One_Copy_Per_Node_Is_Enforced()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1","img1", DesiredState.Running, false, "", ""));

		var act = async () => await _svc.UpsertDeploymentAsync(
			new DeploymentInput(null, "bot", "proj", "n1","img2", DesiredState.Running, false, "", ""));
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task Update_Existing_Deployment_By_Id_Does_Not_Collide_With_Itself()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		var created = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1","img1", DesiredState.Running, false, "", ""));
		var updated = await _svc.UpsertDeploymentAsync(new DeploymentInput(created.Id, "bot", "proj", "n1", "img2", DesiredState.Stopped, false, "", ""));
		updated.Id.Should().Be(created.Id);
		updated.ImageDigest.Should().Be("img2");
		updated.DesiredState.Should().Be(DesiredState.Stopped);
		(await _svc.ListDeploymentsAsync(nodeId: "n1")).Should().HaveCount(1);
	}

	[Fact]
	public async Task Poll_Returns_Assigned_Deployments_And_Bumps_LastSeen()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "net.x", false));
		await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1", "img1", DesiredState.Running, false, "net.x", "env:prod"));

		(await _svc.GetNodeAsync("n1"))!.Online.Should().BeFalse();   // not yet contacted

		var poll = await _svc.PollAsync("n1");
		poll.NodeId.Should().Be("n1");
		poll.Deployments.Should().ContainSingle();
		poll.Deployments[0].Service.Should().Be("bot");
		poll.Deployments[0].Project.Should().Be("proj");
		poll.Deployments[0].ConfigHash.Should().HaveLength(64);

		(await _svc.GetNodeAsync("n1"))!.Online.Should().BeTrue();    // poll bumped LastSeenAt
	}

	[Fact]
	public async Task Heartbeat_Records_Actual_State_On_Deployment_View()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1", "img1", DesiredState.Running, false, "", ""));
		d.ActualState.Should().BeNull();

		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport(
			[new ActualReport("bot", "container123", ActualState.Running, "img1", Healthy: true)]));

		var after = (await _svc.GetDeploymentAsync(d.Id))!;
		after.ActualState.Should().Be(ActualState.Running);
		after.Healthy.Should().BeTrue();
		after.ReportedAt.Should().NotBeNull();
		(await _svc.GetNodeAsync("n1"))!.Online.Should().BeTrue();
	}

	[Fact]
	public async Task Heartbeat_Is_Full_Snapshot_Resets_Absent_Service_To_Missing()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1", "img1", DesiredState.Running, false, "", ""));
		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport([new ActualReport("bot", "c1", ActualState.Running, "img1", Healthy: true)]));
		(await _svc.GetDeploymentAsync(d.Id))!.ActualState.Should().Be(ActualState.Running);

		// next heartbeat no longer reports "bot" (container removed) → must reset to Missing,
		// not leave a stale Running (the deploy-stale-actualstate bug).
		await _svc.ApplyHeartbeatAsync("n1", new HeartbeatReport([]));
		var refreshed = (await _svc.GetDeploymentAsync(d.Id))!;
		refreshed.ActualState.Should().Be(ActualState.Missing);
		refreshed.Healthy.Should().BeFalse();
	}

	[Fact]
	public async Task DeleteNode_Cascades_Deployments()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "", false));
		await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1","img1", DesiredState.Running, false, "", ""));
		(await _svc.DeleteNodeAsync("n1")).Should().BeTrue();
		(await _svc.GetNodeAsync("n1")).Should().BeNull();
		(await _svc.ListDeploymentsAsync()).Should().BeEmpty();
	}

	// directly stamp LastSeenAt to simulate a stale / online node (poll/heartbeat only set "now")
	async Task SetLastSeen(string id, DateTime when) =>
		await _db.Nodes.Where(n => n.Id == id).Set(n => n.LastSeenAt, (DateTime?)when).UpdateAsync();

	[Fact]
	public async Task Reschedule_Moves_Relocatable_Off_Stale_To_TagMatching_Online()
	{
		await _svc.UpsertNodeAsync(new NodeInput("stale", "Stale", "net.x,disk=nvme", false));
		await _svc.UpsertNodeAsync(new NodeInput("fresh", "Fresh", "net.x,disk=nvme", false));
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "stale", "img1", DesiredState.Running, Relocatable: true, "net.x", ""));
		await SetLastSeen("stale", DateTime.UtcNow.AddMinutes(-10));   // stale
		await SetLastSeen("fresh", DateTime.UtcNow);                  // online

		var actions = await _svc.RescheduleStaleAsync(TimeSpan.FromSeconds(90));

		actions.Should().ContainSingle();
		actions[0].Relocated.Should().BeTrue();
		actions[0].ToNode.Should().Be("fresh");
		(await _svc.GetDeploymentAsync(d.Id))!.NodeId.Should().Be("fresh");
	}

	[Fact]
	public async Task Reschedule_Skips_NonRelocatable()
	{
		await _svc.UpsertNodeAsync(new NodeInput("stale", "Stale", "net.x", false));
		await _svc.UpsertNodeAsync(new NodeInput("fresh", "Fresh", "net.x", false));
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "stale", "img1", DesiredState.Running, Relocatable: false, "net.x", ""));
		await SetLastSeen("stale", DateTime.UtcNow.AddMinutes(-10));
		await SetLastSeen("fresh", DateTime.UtcNow);

		(await _svc.RescheduleStaleAsync(TimeSpan.FromSeconds(90))).Should().BeEmpty();
		(await _svc.GetDeploymentAsync(d.Id))!.NodeId.Should().Be("stale");
	}

	[Fact]
	public async Task Reschedule_NoTarget_When_Tags_DoNotMatch()
	{
		await _svc.UpsertNodeAsync(new NodeInput("stale", "Stale", "net.x", false));
		await _svc.UpsertNodeAsync(new NodeInput("fresh", "Fresh", "net.y", false));    // wrong tags
		var d = await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "stale", "img1", DesiredState.Running, Relocatable: true, "net.x", ""));
		await SetLastSeen("stale", DateTime.UtcNow.AddMinutes(-10));
		await SetLastSeen("fresh", DateTime.UtcNow);

		var actions = await _svc.RescheduleStaleAsync(TimeSpan.FromSeconds(90));
		actions.Should().ContainSingle();
		actions[0].Relocated.Should().BeFalse();
		(await _svc.GetDeploymentAsync(d.Id))!.NodeId.Should().Be("stale");   // stays put
	}

	[Fact]
	public async Task Reschedule_Noop_When_Node_Not_Stale()
	{
		await _svc.UpsertNodeAsync(new NodeInput("n1", "N1", "net.x", false));
		await _svc.UpsertDeploymentAsync(new DeploymentInput(null, "bot", "proj", "n1", "img1", DesiredState.Running, Relocatable: true, "net.x", ""));
		await SetLastSeen("n1", DateTime.UtcNow);   // fresh

		(await _svc.RescheduleStaleAsync(TimeSpan.FromSeconds(90))).Should().BeEmpty();
	}
}
