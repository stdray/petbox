using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp.Contract;
using PetBox.Web.Tasks;

namespace PetBox.Tests.Tasks;

// work observation-kind-and-dedup: the observation is a NODE on a code-declared board/kind
// (BoardKind.Observation, MethodologyPresets.ObservationKind), not a new store/row type — this
// covers (1) the board/kind resolve through the ordinary workflow surface, (2) the system-board
// delete/close guard, and (3) the service-layer dedup-with-recurrence guard (reusing
// AutocaptureDedup.FindDuplicateKeyAsync) accumulates a signal instead of creating a duplicate
// node.
public sealed class ObservationKindAndDedupTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly ObservationSignalStore _signals;

	public ObservationKindAndDedupTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-observation-" + Guid.NewGuid().ToString("N"));
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

	[Fact]
	public async Task ObservationBoard_ResolvesTheFourStatusWorkflow()
	{
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);

		var wf = await _tasks.GetBoardWorkflowAsync(Proj, SystemBoards.Observations);

		wf.Kind.Should().Be("observation");
		var block = wf.Workflows.Should().ContainSingle().Subject;
		block.Types.Should().ContainSingle().Which.Should().Be("observation");
		block.Workflow.Statuses.Select(s => s.Slug).Should().Equal("seen", "promoted", "fixed", "declined");
		block.Workflow.Status("seen")!.Kind.Should().Be(StatusKind.Open);
		block.Workflow.Status("promoted")!.Kind.Should().Be(StatusKind.Open);
		block.Workflow.Status("fixed")!.Kind.Should().Be(StatusKind.TerminalOk);
		block.Workflow.Status("declined")!.Kind.Should().Be(StatusKind.TerminalCancel);
		block.Workflow.Transition("seen", "promoted").Should().NotBeNull();
		block.Workflow.Transition("seen", "declined").Should().NotBeNull();
		block.Workflow.Transition("promoted", "fixed").Should().NotBeNull();
		block.Workflow.Transition("promoted", "declined").Should().NotBeNull();
	}

	[Fact]
	public async Task ObservationsBoard_CannotBeDeletedOrClosed()
	{
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);

		var delete = async () => await _tasks.DeleteBoardAsync(Proj, SystemBoards.Observations);
		await delete.Should().ThrowAsync<InvalidOperationException>();

		var close = async () => await _tasks.SetClosedAsync(Proj, SystemBoards.Observations, true);
		await close.Should().ThrowAsync<InvalidOperationException>();

		// Reopening (closed:false) is never blocked — only the freezing half is guarded.
		var reopen = async () => await _tasks.SetClosedAsync(Proj, SystemBoards.Observations, false);
		await reopen.Should().NotThrowAsync();
	}

	[Fact]
	public async Task DedupService_RepeatSighting_AccumulatesRecurrenceInsteadOfANewNode()
	{
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);

		// First sighting: a normal create, then the first-seen seed TasksTools.UpsertAsync would
		// run after a successful non-deduped create.
		var created = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = "obs-1", Version = 0, Title = "Flaky retry in payment webhook", Body = "Saw a 3rd retry loop on the payment webhook under load." }]);
		created.Result.Applied.Should().BeTrue();
		var nodeId = created.Result.Added.Should().ContainSingle().Subject.NodeId;
		await _tasks.RecordObservationFirstSeenAsync(Proj, nodeId);

		var dedup = new ObservationDedupService(_tasks, llm: null);

		// A near-duplicate (different casing/punctuation — the cheap normalized-text pass) must
		// dedup instead of creating "obs-2".
		var outcome = await dedup.PreProcessCreatesAsync(Proj, SystemBoards.Observations,
		[
			new TaskNodeInput { Key = "obs-2", Version = 0, Title = "flaky retry in payment webhook", Body = "saw a 3rd retry loop on the payment webhook under load" },
		]);

		outcome.RemainingNodes.Should().BeEmpty("the duplicate must not reach the normal create path");
		var hit = outcome.Hits.Should().ContainSingle().Subject;
		hit.RequestedKey.Should().Be("obs-2");
		hit.ExistingKey.Should().Be("obs-1");
		hit.ExistingNodeId.Should().Be(nodeId);
		hit.RecurrenceCount.Should().Be(2);

		// A second repeat bumps it again.
		var outcome2 = await dedup.PreProcessCreatesAsync(Proj, SystemBoards.Observations,
		[
			new TaskNodeInput { Key = "obs-3", Version = 0, Title = "Flaky retry in payment webhook", Body = "Saw a 3rd retry loop on the payment webhook under load." },
		]);
		outcome2.Hits.Should().ContainSingle().Which.RecurrenceCount.Should().Be(3);

		// A genuinely different observation is NOT absorbed — it must go through untouched.
		var outcome3 = await dedup.PreProcessCreatesAsync(Proj, SystemBoards.Observations,
		[
			new TaskNodeInput { Key = "obs-4", Version = 0, Title = "Unrelated: stale cache header on /health", Body = "Completely different signal." },
		]);
		outcome3.Hits.Should().BeEmpty();
		outcome3.RemainingNodes.Should().ContainSingle().Which.Key.Should().Be("obs-4");
	}

	[Fact]
	public async Task DedupService_MatchesAgainstAFixedObservationToo_TheRegressionCase()
	{
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);

		var created = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = "obs-1", Version = 0, Title = "Null ref on empty cart checkout", Body = "Repro: empty cart, click checkout, 500." }]);
		var nodeId = created.Result.Added.Should().ContainSingle().Subject.NodeId;
		await _tasks.RecordObservationFirstSeenAsync(Proj, nodeId);

		// Promote then fix, so the dedup pool sees an already-`fixed` observation.
		await _tasks.UpsertAsync(Proj, SystemBoards.Observations, [new NodePatch { Key = "obs-1", Version = 1, Status = "promoted" }]);
		await _tasks.UpsertAsync(Proj, SystemBoards.Observations, [new NodePatch { Key = "obs-1", Version = 2, Status = "fixed" }]);

		var dedup = new ObservationDedupService(_tasks, llm: null);
		var outcome = await dedup.PreProcessCreatesAsync(Proj, SystemBoards.Observations,
		[
			new TaskNodeInput { Key = "obs-5", Version = 0, Title = "Null ref on empty cart checkout", Body = "Repro: empty cart, click checkout, 500." },
		]);

		outcome.Hits.Should().ContainSingle().Which.ExistingKey.Should().Be("obs-1");

		// RecurredAfterFixAt must now be stamped — the regression signal a detector reads.
		var signal = await _signals.GetAsync(Proj, nodeId);
		signal.Should().NotBeNull();
		signal!.RecurredAfterFixAt.Should().NotBeNull();
	}
}
