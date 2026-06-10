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
