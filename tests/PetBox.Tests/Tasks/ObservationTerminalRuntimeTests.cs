using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Contract;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// The LIVE-CONFIGURATION half of observation-edges-promote-and-nail. The sibling suite
// (ObservationEdgesPromoteAndNailTests) homes BOTH boards — observations AND the obligation's —
// in the SAME world (the `$utility` sentinel), so one runtime answers for both and the question
// "which runtime classifies the OBSERVATION's status" cannot be asked, let alone answered wrong.
// That is exactly the blind spot that let the defect ship: on `$system` the obligation board
// (ideas/work) belongs to a METHODOLOGY INSTANCE while `observations` lives in the project's
// `$utility` world, and the two runtimes disagree about the slug `promoted`.
//
// Both boards' worlds here declare a `wiki` kind whose `promoted` is TERMINALOK — verbatim the
// shape `$system` carries (quartet instance + utility layer, both with the doc-promotion wiki
// kind). Under that shape the observation preset's OWN vocabulary must win for a board of kind
// `observation`; if a same-named status from an unrelated kind is allowed to classify it, the
// observation reads as already-terminal and the nail-on-fix effect silently does nothing —
// exactly what the live sweep saw (observation stuck at `promoted`, no fixedByNodeId).
public sealed class ObservationTerminalRuntimeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;
	readonly ObservationSignalStore _signals;

	public ObservationTerminalRuntimeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-observation-runtime-" + Guid.NewGuid().ToString("N"));
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

	// `$system`'s doc-promotion wiki kind, reduced to the one property that matters here: it
	// owns the slug `promoted`, and for a wiki PAGE that slug is TERMINAL (promoted to /doc).
	// For an OBSERVATION the same slug is OPEN (promoted into an obligation, still owed).
	static MethodologyKindDef WikiKind() => new("wiki", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(
			["page"],
			[
				new WorkflowStatus("draft", "Draft", StatusKind.Open),
				new WorkflowStatus("live", "Live", StatusKind.Open),
				new WorkflowStatus("promoted", "Promoted to /doc", StatusKind.TerminalOk),
				new WorkflowStatus("stale", "Stale", StatusKind.TerminalCancel),
			],
			[new MethodologyTransitionDef("draft", "live"), new MethodologyTransitionDef("live", "promoted")]),
	]);

	// The live shape: an obligation board inside a methodology INSTANCE whose rules know nothing
	// about kind `observation`, and the system `observations` board in the project's `$utility`
	// world — whose own layer likewise declares only `wiki`.
	async Task SeedLiveShapedProjectAsync()
	{
		await _tasks.DefineMethodologyAsync(Proj, new MethodologyDefinition("utility", [WikiKind()]), 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "quartet", "builtin", "quartet");
		var rules = (await _tasks.GetMethodologyInstanceRulesAsync(Proj, "quartet"))!;
		await _tasks.DefineMethodologyInstanceRulesAsync(Proj, "quartet",
			rules.Definition with { Kinds = [.. rules.Definition.Kinds, WikiKind()] }, rules.Version);
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);
	}

	// Every write here passes the board's CURRENT version as its optimistic baseline: these tests
	// seed several nodes per board, so a hardcoded 0/1 would go stale the moment a second node
	// lands and the upsert would be silently rejected rather than tested.
	async Task<long> VersionOfAsync(string board) => (await _tasks.GetAsync(Proj, board)).CurrentVersion;

	async Task<string> CreateObservationAsync(string key)
	{
		var created = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = key, Version = await VersionOfAsync(SystemBoards.Observations), Title = "Flaky auth hash under load", Body = "Saw it twice." }]);
		created.Result.Applied.Should().BeTrue();
		return created.Result.Added.Should().ContainSingle().Subject.NodeId;
	}

	// The two writes TasksTools.ObservationPromoteAsync performs, against the INSTANCE's work board.
	async Task<(string ObligationNodeId, string ObligationKey)> PromoteOntoInstanceBoardAsync(
		string observationKey, string obligationKey)
	{
		var created = await _tasks.UpsertAsync(Proj, "work",
			[new NodePatch { Key = obligationKey, Version = await VersionOfAsync("work"), Type = "chore", Title = "Fix the flaky auth hash" }]);
		created.Result.Applied.Should().BeTrue();
		var obligation = created.Result.Added.Should().ContainSingle().Subject;

		var promoted = await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
		[
			new NodePatch
			{
				Key = observationKey,
				Version = await VersionOfAsync(SystemBoards.Observations),
				Status = "promoted",
				Links = new Dictionary<string, IReadOnlyList<string>> { [MethodologyPresets.ObservationObligationLinkKind] = [obligation.NodeId] },
			},
		]);
		promoted.Result.Applied.Should().BeTrue();
		return (obligation.NodeId, obligation.Key);
	}

	async Task DriveToAsync(string obligationKey, params string[] statuses)
	{
		foreach (var status in statuses)
		{
			var r = await _tasks.UpsertAsync(Proj, "work",
				[new NodePatch { Key = obligationKey, Version = await VersionOfAsync("work"), Status = status }]);
			r.Result.Applied.Should().BeTrue();
		}
	}

	// The exact live sweep that found the defect: promote, then close the obligation the ordinary
	// way. Before the fix the observation stayed at `promoted` with no fixedByNodeId, because the
	// wiki kind's TERMINALOK `promoted` classified the OBSERVATION's status.
	[Fact]
	public async Task ObligationOnAnInstanceBoard_ReachingTerminalOk_StillFixesTheUtilityHomedObservation()
	{
		await SeedLiveShapedProjectAsync();
		var obsId = await CreateObservationAsync("live-check-flaky-auth-hash");
		var (obligationId, obligationKey) = await PromoteOntoInstanceBoardAsync("live-check-flaky-auth-hash", "chore-live-1");

		await DriveToAsync(obligationKey, "InProgress", "Review", "Done");

		var view = await _tasks.GetNodeAsync(Proj, obsId);
		view!.Node.Status.Should().Be("fixed",
			"the observation's status is classified by the OBSERVATION vocabulary, not by a same-named wiki status");

		var signal = await _signals.GetAsync(Proj, obsId);
		signal!.FixedByNodeId.Should().Be(obligationId);
		signal.FixedAt.Should().NotBeNull();
	}

	// The regression half of the same function — it reads the identical isTerminal verdict, so it
	// was broken by the identical cause and is not proven by the terminal-OK case alone.
	[Fact]
	public async Task ObligationOnAnInstanceBoard_ReachingTerminalCancel_StillReturnsTheObservationToSeen()
	{
		await SeedLiveShapedProjectAsync();
		var obsId = await CreateObservationAsync("live-check-abandoned");
		var (_, obligationKey) = await PromoteOntoInstanceBoardAsync("live-check-abandoned", "chore-live-2");

		await DriveToAsync(obligationKey, "Cancelled");

		var view = await _tasks.GetNodeAsync(Proj, obsId);
		view!.Node.Status.Should().Be("seen", "abandoned, not fixed — the observation stays open for another attempt");

		var signal = await _signals.GetAsync(Proj, obsId);
		if (signal is not null)
			signal.FixedByNodeId.Should().BeNull();
	}

	// The AUTHORITY edge, isolated. Here the instance declares its OWN kind literally named
	// `observation` whose `promoted` is terminal, while the `$utility` layer — the world the
	// `observations` board actually belongs to — declares the real observation vocabulary. Only a
	// call that resolves the OBSERVATIONS BOARD's runtime gets this right; passing the obligation
	// board's runtime through reads the instance's homonym and does nothing.
	[Fact]
	public async Task TheObservationsBoardsOwnRuntimeClassifiesIt_NotTheObligationBoards()
	{
		var homonym = new MethodologyKindDef(SystemBoards.ObservationKind, QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(
				["observation"],
				[
					new WorkflowStatus("seen", "Seen", StatusKind.Open),
					new WorkflowStatus("promoted", "Promoted", StatusKind.TerminalOk),
				],
				[new MethodologyTransitionDef("seen", "promoted")]),
		]);
		var real = new MethodologyKindDef(SystemBoards.ObservationKind, QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(
				["observation"],
				[
					new WorkflowStatus("seen", "Seen", StatusKind.Open),
					new WorkflowStatus("promoted", "Promoted", StatusKind.Open),
					new WorkflowStatus("fixed", "Fixed", StatusKind.TerminalOk),
					new WorkflowStatus("declined", "Declined", StatusKind.TerminalCancel),
				],
				[new MethodologyTransitionDef("seen", "promoted"), new MethodologyTransitionDef("promoted", "fixed")]),
		]);

		await _tasks.DefineMethodologyAsync(Proj, new MethodologyDefinition("utility", [real]), 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "quartet", "builtin", "quartet");
		var rules = (await _tasks.GetMethodologyInstanceRulesAsync(Proj, "quartet"))!;
		await _tasks.DefineMethodologyInstanceRulesAsync(Proj, "quartet",
			rules.Definition with { Kinds = [.. rules.Definition.Kinds, homonym] }, rules.Version);
		await _tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs", null,
			methodologyInstance: TaskBoardMeta.UtilityWorld);

		var obsId = await CreateObservationAsync("authority-check");
		var (obligationId, obligationKey) = await PromoteOntoInstanceBoardAsync("authority-check", "chore-authority");

		await DriveToAsync(obligationKey, "InProgress", "Review", "Done");

		var view = await _tasks.GetNodeAsync(Proj, obsId);
		view!.Node.Status.Should().Be("fixed");
		(await _signals.GetAsync(Proj, obsId))!.FixedByNodeId.Should().Be(obligationId);
	}

	async Task<IReadOnlyList<string>> ListObservationKeysAsync(IReadOnlyList<string>? statusKind)
	{
		var r = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: SystemBoards.Observations, StatusKind: statusKind),
			Limit = 100,
		});
		return r.Hits.Select(h => h.Node.Key).ToList();
	}

	// The SECOND live symptom of the same root, and the one no existing test could express: a
	// promoted observation vanished from `tasks_search`'s default listing. The default listing's
	// visibility predicate is MethodologyRuntime.IsTerminalStatus(kind, status) — the very verdict
	// the nail-on-fix effect reads — so a `promoted` misjudged as terminal is both un-nailable AND
	// invisible. The `seen` node is the control: the INITIAL status was classified correctly all
	// along (nothing else owns that slug), which is exactly why the board looked healthy.
	// observation-promotes-to-commitment requires a promoted observation to stay addressable; being
	// reachable only by exact key while absent from every default read does not satisfy that.
	[Fact]
	public async Task DefaultListing_ShowsAPromotedObservation_AndHidesATerminalOne()
	{
		await SeedLiveShapedProjectAsync();
		await CreateObservationAsync("still-seen");
		await CreateObservationAsync("was-promoted");
		await PromoteOntoInstanceBoardAsync("was-promoted", "chore-listing");
		await CreateObservationAsync("was-declined");
		(await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = "was-declined", Version = await VersionOfAsync(SystemBoards.Observations), Status = "declined", Reason = "not worth acting on" }]))
			.Result.Applied.Should().BeTrue();

		(await ListObservationKeysAsync(null)).Should()
			.Contain("still-seen").And.Contain("was-promoted", "`promoted` is OPEN for an observation — it is owed, not done")
			.And.NotContain("was-declined", "`declined` is terminalcancel and stays out of the default listing");
	}

	// The stored-facet half of the same predicate: search_meta carries the statusKind stamped at
	// write time, and an EXPLICIT statusKind selects against that row rather than reclassifying on
	// read. Both spellings must agree with the observation vocabulary, or the two doors disagree
	// about the same node.
	[Fact]
	public async Task ExplicitStatusKindFacet_ClassifiesPromotedAsOpen_AndDeclinedAsTerminalCancel()
	{
		await SeedLiveShapedProjectAsync();
		await CreateObservationAsync("facet-promoted");
		await PromoteOntoInstanceBoardAsync("facet-promoted", "chore-facet");
		await CreateObservationAsync("facet-declined");
		(await _tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = "facet-declined", Version = await VersionOfAsync(SystemBoards.Observations), Status = "declined", Reason = "not worth acting on" }]))
			.Result.Applied.Should().BeTrue();

		(await ListObservationKeysAsync(["open"])).Should().Contain("facet-promoted").And.NotContain("facet-declined");
		(await ListObservationKeysAsync(["terminalcancel"])).Should().Contain("facet-declined").And.NotContain("facet-promoted");
	}
}
