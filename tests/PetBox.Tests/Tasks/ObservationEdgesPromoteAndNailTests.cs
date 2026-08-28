using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// work observation-edges-promote-and-nail: the two typed edges built on top of
// observation-kind-and-dedup. This covers the SERVICE-LAYER halves directly (the MCP tool,
// TasksTools.ObservationPromoteAsync, is a thin adapter over the same ITasksService calls):
//   (1) a promoted observation carries an `observation_obligation` edge to its obligation and
//       stays addressable (status `promoted`, never deleted);
//   (2) the obligation reaching a terminal-OK status on the NORMAL tasks_upsert path
//       automatically fixes the observation (status -> `fixed`, observation_signal stamped);
//   (3) the obligation reaching a terminal-CANCEL status instead returns the observation to
//       `seen` (abandoned, not fixed) with no FixedByNodeId stamp.
public sealed class ObservationEdgesPromoteAndNailTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly ObservationSignalStore _signals;
	readonly RelationStore _relations;

	public ObservationEdgesPromoteAndNailTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-observation-edges-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_signals = new ObservationSignalStore(_factory);
		_relations = new RelationStore(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), _relations,
			new TagStore(_factory), new CommentService(_factory), observationSignals: _signals);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	async Task<string> CreateObservationSeenAsync(string key)
	{
		await EnsureObservationsBoardAsync();
		var created = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = key, Version = 0, Title = "Flaky retry in payment webhook", Body = "Saw a retry loop under load." }]);
		created.Result.Applied.Should().BeTrue();
		return created.Result.Added.Should().ContainSingle().Subject.NodeId;
	}

	bool _observationsBoardReady;
	async Task EnsureObservationsBoardAsync()
	{
		if (_observationsBoardReady) return;
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);
		_observationsBoardReady = true;
	}

	bool _workBoardReady;
	async Task EnsureWorkBoardAsync()
	{
		if (_workBoardReady) return;
		await _tasks.CreateBoardAsync(Proj, "work", "work", "work", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);
		_workBoardReady = true;
	}

	// The exact two writes TasksTools.ObservationPromoteAsync performs: create the obligation on
	// its own board, then patch the observation (status -> promoted, links.observation_obligation
	// -> the obligation's NodeId).
	async Task<(string ObservationNodeId, string ObligationNodeId, string ObligationKey)> PromoteAsync(
		string observationKey, string observationNodeId, string obligationKey)
	{
		await EnsureWorkBoardAsync();
		var created = await _tasks.UpsertAsync(Proj, "work",
			[new NodePatch { Key = obligationKey, Version = 0, Type = "chore", Title = "Fix the flaky retry" }]);
		created.Result.Applied.Should().BeTrue();
		var obligation = created.Result.Added.Should().ContainSingle().Subject;

		var promoted = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
		[
			new NodePatch
			{
				Key = observationKey,
				Version = 1,
				Status = "promoted",
				Links = new Dictionary<string, IReadOnlyList<string>> { [MethodologyPresets.ObservationObligationLinkKind] = [obligation.NodeId] },
			},
		]);
		promoted.Result.Applied.Should().BeTrue();
		return (observationNodeId, obligation.NodeId, obligation.Key);
	}

	[Fact]
	public async Task Promote_LinksTheObservationToTheObligation_AndKeepsItAddressable()
	{
		var obsId = await CreateObservationSeenAsync("obs-1");
		var (_, obligationId, _) = await PromoteAsync("obs-1", obsId, "chore-1");

		var edges = await _relations.ListAsync(Proj, obsId, "from");
		var edge = edges.Should().ContainSingle(e => e.Kind == MethodologyPresets.ObservationObligationLinkKind).Subject;
		edge.FromNodeId.Should().Be(obsId);
		edge.ToNodeId.Should().Be(obligationId);

		// The observation is NOT deleted — it stays addressable at `promoted`.
		var view = await _tasks.GetNodeAsync(Proj, obsId);
		view.Should().NotBeNull();
		view!.Node.Status.Should().Be("promoted");
	}

	[Fact]
	public async Task Obligation_ReachingTerminalOk_AutomaticallyFixesTheObservation()
	{
		var obsId = await CreateObservationSeenAsync("obs-2");
		var (_, obligationId, obligationKey) = await PromoteAsync("obs-2", obsId, "chore-2");

		// Pending -> InProgress -> Review -> Done, the ordinary tasks_upsert path — no separate call.
		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = obligationKey, Version = 1, Status = "InProgress" }]);
		await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = obligationKey, Version = 2, Status = "Review" }]);
		var done = await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = obligationKey, Version = 3, Status = "Done" }]);
		done.Result.Applied.Should().BeTrue();

		var view = await _tasks.GetNodeAsync(Proj, obsId);
		view!.Node.Status.Should().Be("fixed");

		var signal = await _signals.GetAsync(Proj, obsId);
		signal.Should().NotBeNull();
		signal!.FixedByNodeId.Should().Be(obligationId);
		signal.FixedAt.Should().NotBeNull();
	}

	[Fact]
	public async Task Obligation_ReachingTerminalCancel_ReturnsTheObservationToSeen_NotFixed()
	{
		var obsId = await CreateObservationSeenAsync("obs-3");
		var (_, _, obligationKey) = await PromoteAsync("obs-3", obsId, "chore-3");

		var cancelled = await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = obligationKey, Version = 1, Status = "Cancelled" }]);
		cancelled.Result.Applied.Should().BeTrue();

		var view = await _tasks.GetNodeAsync(Proj, obsId);
		view!.Node.Status.Should().Be("seen");

		// Abandoned, not fixed — no FixedByNodeId stamp.
		var signal = await _signals.GetAsync(Proj, obsId);
		if (signal is not null)
			signal.FixedByNodeId.Should().BeNull();
	}
}
