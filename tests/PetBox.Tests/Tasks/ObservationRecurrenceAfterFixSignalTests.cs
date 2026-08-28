using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// work observation-recurrence-after-fix-signal: the three things this card adds on top of
// the already-merged observation-kind-and-dedup / observation-edges-promote-and-nail cards:
//   (1) the recurrence signal (RecurrenceCount/RecurredAfterFixAt/FixedByNodeId) is READABLE
//       on a TaskNodeView row (tasks_node_get / tasks_search), kind `observation` ONLY;
//   (2) a recurred-after-fix observation ranks ABOVE a plain sighting in tasks_search's
//       existing order, and the observation itself reopens fixed -> seen;
//   (3) the obligation named by FixedByNodeId gets decisionPending:true — "fixed, and it came
//       back" lands in the OWNER's queue, the observation's own status never lies about it
//       being auto-reopened (owner decision: the task's FSM/status is never touched here).
public sealed class ObservationRecurrenceAfterFixSignalTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly ObservationSignalStore _signals;

	public ObservationRecurrenceAfterFixSignalTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-observation-recurrence-signal-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_signals = new ObservationSignalStore(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory),
			new TagStore(_factory), new CommentService(_factory), observationSignals: _signals);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
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

	async Task<string> CreateObservationSeenAsync(string key, string title)
	{
		await EnsureObservationsBoardAsync();
		var created = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = key, Version = 0, Title = title, Body = "repro details" }]);
		created.Result.Applied.Should().BeTrue();
		var nodeId = created.Result.Added.Should().ContainSingle().Subject.NodeId;
		await _tasks.RecordObservationFirstSeenAsync(Proj, nodeId);
		return nodeId;
	}

	async Task<string> CreateObligationAsync(string key)
	{
		await EnsureWorkBoardAsync();
		var created = await _tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = key, Version = 0, Type = "chore", Title = "Fix it" }]);
		created.Result.Applied.Should().BeTrue();
		return created.Result.Added.Should().ContainSingle().Subject.NodeId;
	}

	// Drives an observation `seen` -> `promoted` -> `fixed` (the only legal FSM path to
	// `fixed`), stamping FixedByNodeId the same way SyncObservationOnObligationTerminalAsync
	// does (MarkFixedAsync) — without needing the full promote+obligation-upsert dance, since
	// that path is already covered end-to-end by ObservationEdgesPromoteAndNailTests. Reads the
	// CAS baseline fresh before each write rather than assuming a version number: `Version` is
	// the BOARD-wide cursor (TemporalStore.UpsertAsync's `nextVersion`, partitioned by board),
	// not a per-node counter — a sibling node created earlier on the same board already moved it.
	async Task FixAsync(string key, string obsNodeId, string fixedByNodeId)
	{
		var v1 = (await _tasks.GetNodeAsync(Proj, obsNodeId))!.Node.Version;
		var promoted = await _tasks.UpsertAsync(Proj, SystemBoards.Observations, [new NodePatch { Key = key, Version = v1, Status = "promoted" }]);
		promoted.Result.Applied.Should().BeTrue();
		var v2 = (await _tasks.GetNodeAsync(Proj, obsNodeId))!.Node.Version;
		var fixedNow = await _tasks.UpsertAsync(Proj, SystemBoards.Observations, [new NodePatch { Key = key, Version = v2, Status = "fixed" }]);
		fixedNow.Result.Applied.Should().BeTrue();
		await _signals.MarkFixedAsync(Proj, obsNodeId, fixedByNodeId);
	}

	[Fact]
	public async Task RecurrenceAfterFix_ReopensTheObservationToSeen_AndFlagsTheFixerDecisionPending()
	{
		var obligationId = await CreateObligationAsync("chore-1");
		var obsId = await CreateObservationSeenAsync("obs-1", "Null ref on empty cart checkout");
		await FixAsync("obs-1", obsId, fixedByNodeId: obligationId);

		// Sanity: the obligation starts clean (no owner-decision flag yet).
		(await _tasks.GetNodeAsync(Proj, obligationId))!.Node.DecisionPending.Should().BeFalse();

		// The regression: a repeat sighting lands on the now-`fixed` observation.
		var count = await _tasks.RecordObservationRecurrenceAsync(Proj, obsId, currentlyFixed: true);
		count.Should().Be(2);

		// (2)+ownership split: the OBSERVATION reopens automatically...
		var obs = await _tasks.GetNodeAsync(Proj, obsId);
		obs!.Node.Status.Should().Be("seen");

		// ...the TASK that fixed it is never auto-reopened (still whatever status it had)...
		var obligation = await _tasks.GetNodeAsync(Proj, obligationId);
		obligation!.Node.Status.Should().Be("Pending");
		// ...but IS flagged for the owner.
		obligation.Node.DecisionPending.Should().BeTrue();

		// (1): the signal itself is stamped and, per the next test, readable on the row.
		var signal = await _signals.GetAsync(Proj, obsId);
		signal!.RecurredAfterFixAt.Should().NotBeNull();
		signal.FixedByNodeId.Should().Be(obligationId);
	}

	[Fact]
	public async Task RecurrenceOnAnOpenObservation_NeverTouchesStatusOrAnyTask()
	{
		var obsId = await CreateObservationSeenAsync("obs-2", "Flaky retry in payment webhook");

		var count = await _tasks.RecordObservationRecurrenceAsync(Proj, obsId, currentlyFixed: false);
		count.Should().Be(2);

		var obs = await _tasks.GetNodeAsync(Proj, obsId);
		obs!.Node.Status.Should().Be("seen"); // unchanged — was never `fixed` to begin with

		var signal = await _signals.GetAsync(Proj, obsId);
		signal!.RecurredAfterFixAt.Should().BeNull();
	}

	[Fact]
	public async Task ObservationSignal_IsReadableOnGetNodeAsync_AndOnSearchNodesAsync_KindObservationOnly()
	{
		var obligationId = await CreateObligationAsync("chore-2");
		var obsId = await CreateObservationSeenAsync("obs-3", "Stale cache header on /health");
		await FixAsync("obs-3", obsId, fixedByNodeId: obligationId);
		await _tasks.RecordObservationRecurrenceAsync(Proj, obsId, currentlyFixed: true);

		// tasks_node_get path (GetNodeAsync).
		var detail = await _tasks.GetNodeAsync(Proj, obsId);
		detail!.Node.Observation.Should().NotBeNull();
		detail.Node.Observation!.RecurrenceCount.Should().Be(2);
		detail.Node.Observation.RecurredAfterFixAt.Should().NotBeNull();
		detail.Node.Observation.FixedByNodeId.Should().Be(obligationId);

		// tasks_search LISTING path (board-scoped, GetAsyncCore -> GetAsync).
		var listing = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: SystemBoards.Observations),
		});
		var row = listing.Hits.Should().ContainSingle(h => h.Node.Key == "obs-3").Subject;
		row.Node.Observation.Should().NotBeNull();
		row.Node.Observation!.RecurrenceCount.Should().Be(2);

		// tasks_search QUERY path (lean rows) must ALSO carry it — the whole point of the card.
		var query = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Query = "Stale cache header",
			Filter = new TaskNodeFilter(Board: SystemBoards.Observations),
		});
		var queryRow = query.Hits.Should().ContainSingle(h => h.Node.Key == "obs-3").Subject;
		queryRow.Node.Observation.Should().NotBeNull();
		queryRow.Node.Observation!.RecurredAfterFixAt.Should().NotBeNull();

		// A non-observation board never carries it, even though the field exists on every row.
		var obligationDetail = await _tasks.GetNodeAsync(Proj, obligationId);
		obligationDetail!.Node.Observation.Should().BeNull();
	}

	[Fact]
	public async Task RecurredObservation_RanksAboveAPlainSighting_InTheDefaultListingOrder()
	{
		// Same priority (default), so the default listing tiebreak (key, ordinal) would put
		// "obs-a" BEFORE "obs-b" — this test proves the recurrence boost overrides exactly that.
		var obligationId = await CreateObligationAsync("chore-3");
		await CreateObservationSeenAsync("obs-a", "Unrelated: stale cache header");
		var obsB = await CreateObservationSeenAsync("obs-b", "Null ref on empty cart checkout");
		await FixAsync("obs-b", obsB, fixedByNodeId: obligationId);
		await _tasks.RecordObservationRecurrenceAsync(Proj, obsB, currentlyFixed: true); // reopens obs-b to `seen`

		var listing = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: SystemBoards.Observations),
		});

		listing.Hits.Select(h => h.Node.Key).Should().Equal("obs-b", "obs-a");
	}
}
