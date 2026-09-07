using LinqToDB;
using Microsoft.Extensions.Logging;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// work blocks-edge-closes-on-terminal-blocker: a node that ENTERS a terminal status by its OWN
// board's FSM soft-closes every active OUTGOING `blocks` edge — on ANY kind, whether or not that
// kind declares a blocks effect, a BlocksGate, or a methodology document at all; and for ANY
// terminal, Cancelled included. The dependent's release (if its kind declares a gate) is judged on
// the DEPENDENT's board, not the blocker's.
//
// Universal on KIND, not on HOW THE NODE GOT THERE: the rule hangs off UpsertAsync, so a node
// driven into a terminal status by a declared effect's CASCADE (SetActiveNodeStatusAsync, which
// writes straight to TemporalStore) keeps its outgoing edges. Known gap, its own card, and NOT
// covered by anything below — no test here should be read as claiming otherwise.
//
// Before this rule, only the `work` kind's declared `On: Done, Link: blocks` effect ever closed
// such an edge, so on `simple`/`classic`/a project-declared kind `blockedBy` named a finished
// blocker forever (observation blocks-edge-never-closes-on-kind-without-blocksgate). Each test
// below is written so it goes RED with the rule removed — see the per-test RED note.
public sealed class TerminalBlockerBlocksEdgeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public TerminalBlockerBlocksEdgeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-termblock-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_relations = new RelationStore(_factory);
		_tasks = new TasksService(_store, _relations, new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static NodePatch Node(string key, string? blockedBy = null, string? status = null, long version = 0, string? type = null) => new()
	{
		Key = key,
		Title = key,
		Body = "body of " + key,
		BlockedBy = blockedBy,
		Status = status,
		Type = type,
		Version = version,
	};

	// Straight off the store, not through GetAsync: a board VIEW hides terminal nodes, and half
	// these fixtures need the id of a node that is already Done.
	string NodeId(string board, string key) =>
		_store.GetContext(Proj).TaskNodes.Single(n => n.Board == board && n.Key == key && n.ActiveTo == null).NodeId;

	// Rewrite a node's status straight in the store, bypassing UpsertAsync entirely. This is how a
	// fixture reproduces the FLEET state the backfill exists for: a blocker that reached a terminal
	// status BEFORE the engine rule shipped, so no transition ever happened for the rule to hook.
	void ForceStatus(string nodeId, string status) =>
		_store.GetContext(Proj).TaskNodes
			.Where(n => n.NodeId == nodeId && n.ActiveTo == null)
			.Set(n => n.Status, status)
			.Update();

	string StatusOf(string nodeId) =>
		_store.GetContext(Proj).TaskNodes.Single(n => n.NodeId == nodeId && n.ActiveTo == null).Status;

	Task<IReadOnlyList<Relation>> ActiveBlocksInto(string nodeId) =>
		BlocksInto(nodeId, includeHistory: false);

	async Task<IReadOnlyList<Relation>> BlocksInto(string nodeId, bool includeHistory) =>
		[.. (await _relations.ListAsync(Proj, nodeId, "to", includeHistory)).Where(e => e.Kind == "blocks")];

	// Blocker + dependent on ONE board of `kind`; the dependent names the blocker via blockedBy.
	// Returns (blockerId, dependentId).
	async Task<(string Blocker, string Dependent)> Pair(
		string board, string kind, string blockerStatus, string dependentStatus, string? type = null)
	{
		await _tasks.CreateBoardAsync(Proj, board, kind, null, null);
		await _tasks.UpsertAsync(Proj, board, new[] { Node("blocker", status: blockerStatus, type: type) });
		var blockerId = NodeId(board, "blocker");
		await _tasks.UpsertAsync(Proj, board, new[] { Node("dep", blockedBy: blockerId, status: dependentStatus, type: type) });
		return (blockerId, NodeId(board, "dep"));
	}

	// ---------------------------------------------------------------------------------------
	// The card's list, one test per line.
	// ---------------------------------------------------------------------------------------

	// RED without the rule: `classic` declares no Effects at all, so nothing ever closed this edge
	// — the first assertion fails with the edge still active. The status assertion is the CONTROL:
	// classic declares no BlocksGate, so a green run must ALSO show the dependent untouched. A
	// version of this rule that released indiscriminately would fail here rather than pass.
	[Fact]
	public async Task Classic_TerminalBlocker_ClosesEdge_AndLeavesDependentStatusAlone()
	{
		var (blockerId, depId) = await Pair("cboard", "classic", "Review", "Todo");

		await _tasks.UpsertAsync(Proj, "cboard", new[] { Node("blocker", status: "Done", version: 1) });

		StatusOf(blockerId).Should().Be("Done", "control: the blocker really did enter the terminal status");
		(await ActiveBlocksInto(depId)).Should().BeEmpty("a terminal blocker releases its edge on classic too");
		StatusOf(depId).Should().Be("Todo", "classic declares no BlocksGate — the dependent's status is not the rule's business");
	}

	// RED without the rule: same shape on `simple`, the kind the observation was filed against.
	// `Blocked` staying put is the CONTROL — simple carries a Blocked STATUS but declares no
	// BlocksGate, so closing the edge must not drag the status with it.
	[Fact]
	public async Task Simple_TerminalBlocker_ClosesEdge_AndBlockedStaysBlocked()
	{
		var (blockerId, depId) = await Pair("sboard", "simple", "Todo", "Blocked");

		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("blocker", status: "Done", version: 1) });

		StatusOf(blockerId).Should().Be("Done", "control: the blocker really did enter the terminal status");
		(await ActiveBlocksInto(depId)).Should().BeEmpty();
		StatusOf(depId).Should().Be("Blocked", "no BlocksGate on `simple` -> status left as-is");
	}

	// CONTROL / behaviour-lock, NOT a red test: `work` declares `On: Done, Link: blocks` already,
	// so this passes both with and without the new rule. It is here to prove the new sweep did not
	// PRE-EMPT or double-fire on the declared effect — it runs AFTER RunTransitionEffectsAsync, so
	// by the time it looks there is no active edge left and the declared Set/OnlyFrom semantics are
	// what produced `InProgress`.
	[Fact]
	public async Task Work_DoneBlocker_StillReleasesViaTheDeclaredEffect()
	{
		// Born at Review, not Pending: work's FSM reaches Done ONLY from Review.
		var (blockerId, depId) = await Pair("wboard", "work", "Review", "Blocked", type: "chore");

		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("blocker", status: "Done", version: 1, type: "chore") });

		StatusOf(blockerId).Should().Be("Done");
		(await ActiveBlocksInto(depId)).Should().BeEmpty();
		StatusOf(depId).Should().Be("InProgress");
	}

	// RED without the rule: this is the Cancelled half the card calls out. WorkKind's declared
	// effect is `On: "Done"` ONLY, so a Cancelled blocker released nothing — the dependent sat in
	// `Blocked` naming a blocker that will never move, which GuardEngine.RequireBlockers then
	// re-flags on every later edit. Both assertions fail without the rule (edge active, status
	// still Blocked).
	[Fact]
	public async Task Work_CancelledBlocker_AlsoClosesEdge_AndReleasesDependent()
	{
		var (blockerId, depId) = await Pair("wboard", "work", "Pending", "Blocked", type: "chore");

		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("blocker", status: "Cancelled", version: 1, type: "chore") });

		StatusOf(blockerId).Should().Be("Cancelled", "control: a TerminalCancel status, not TerminalOk");
		(await ActiveBlocksInto(depId)).Should().BeEmpty("ANY terminal closes the edge, Cancelled included");
		StatusOf(depId).Should().Be("InProgress", "work declares BlocksGate(Blocked -> InProgress) and this was the last blocker");
	}

	// The partial-state guard: closing ONE edge must not release a dependent that is still blocked
	// by another. Without the rule this test is red on `edge1 closed` for the Cancelled blocker;
	// with a rule that released on ANY closure rather than on the LAST one it would be red on the
	// status. Both failure directions are covered, which is what makes green here mean something.
	[Fact]
	public async Task Work_SecondBlockerStillActive_ClosesOnlyItsOwnEdge_AndDoesNotRelease()
	{
		await _tasks.CreateBoardAsync(Proj, "wboard", "work", null, null);
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("b1", status: "Pending", type: "chore"), Node("b2", status: "Pending", type: "chore") });
		var b1 = NodeId("wboard", "b1");
		var b2 = NodeId("wboard", "b2");
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("dep", blockedBy: b1, status: "Blocked", type: "chore") });
		var depId = NodeId("wboard", "dep");
		await _relations.CreateAsync(Proj, "blocks", b2, depId);
		(await ActiveBlocksInto(depId)).Should().HaveCount(2, "control: the fixture really did wire two blockers");

		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("b1", status: "Cancelled", version: 1, type: "chore") });

		var remaining = await ActiveBlocksInto(depId);
		remaining.Should().ContainSingle().Which.FromNodeId.Should().Be(b2, "only the terminal blocker's own edge closes");
		StatusOf(depId).Should().Be("Blocked", "a dependent with a live blocker left is NOT released");
	}

	// CROSS-BOARD, and the sharp version of it: the dependent lives on a board belonging to a
	// methodology INSTANCE whose declared kind carries a BlocksGate under custom status names
	// (Waiting -> Running); the blocker lives on a plain `simple` board OUTSIDE that instance.
	//
	// RED without the rule: the edge never closes at all.
	// RED without the runtime fix specifically: resolving BlocksGate off the BLOCKER's runtime
	// (the pre-existing delete-path flaw the card says to fix in passing) asks the project-level
	// document about kind `gated`, which does not declare it there — the lookup falls through to
	// MethodologyPresets, finds no gate, and the dependent is left stranded in `Waiting`. Only
	// resolving on the DEPENDENT's own board produces `Running`.
	[Fact]
	public async Task CrossBoard_DependentGateIsResolvedOnTheDependentsOwnBoard()
	{
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "tmpl-gated", GatedDef("tmpl-gated", "gated"), 0);
		var instance = await _tasks.CreateMethodologyInstanceAsync(Proj, "inst", "template", "tmpl-gated");
		var gatedBoard = instance.Boards.Single().Name;

		await _tasks.CreateBoardAsync(Proj, "plain", "simple", null, null, methodologyInstance: "$utility");
		await _tasks.UpsertAsync(Proj, "plain", new[] { Node("blocker", status: "Todo") });
		var blockerId = NodeId("plain", "blocker");
		await _tasks.UpsertAsync(Proj, gatedBoard, new[] { Node("dep", blockedBy: blockerId, status: "Waiting", type: "task") });
		var depId = NodeId(gatedBoard, "dep");

		// Control: the two boards really do resolve DIFFERENT documents — without this, the test
		// could pass while proving nothing about which runtime answered.
		(await _tasks.GetRuntimeForBoardAsync(Proj, gatedBoard)).IsDefinedKind("gated").Should().BeTrue();
		(await _tasks.GetRuntimeForBoardAsync(Proj, "plain")).IsDefinedKind("gated").Should().BeFalse();

		await _tasks.UpsertAsync(Proj, "plain", new[] { Node("blocker", status: "Done", version: 1) });

		(await ActiveBlocksInto(depId)).Should().BeEmpty("a terminal blocker releases its edge across board boundaries");
		StatusOf(depId).Should().Be("Running", "the gate came from the DEPENDENT's instance document, not the blocker's board");
	}

	// The owner's conscious limitation, pinned so a later change cannot quietly "fix" it: reopening
	// the blocker does NOT restore the edge (that is already how `work` behaves; the history stays
	// readable via ClosedAt). This is a lock test — it must stay green, and it also proves the rule
	// is not re-firing destructively on every subsequent touch of an already-terminal node.
	[Fact]
	public async Task ReopeningTheBlocker_DoesNotRestoreTheEdge_ButHistoryKeepsIt()
	{
		var (_, depId) = await Pair("sboard", "simple", "Todo", "Blocked");
		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("blocker", status: "Done", version: 1) });
		(await ActiveBlocksInto(depId)).Should().BeEmpty();

		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("blocker", status: "Todo", version: 2) });

		(await ActiveBlocksInto(depId)).Should().BeEmpty("reopen does not restore the edge — accepted deliberately");
		(await BlocksInto(depId, includeHistory: true)).Should().ContainSingle()
			.Which.ClosedAt.Should().NotBeNull("the edge is soft-closed, so the history still reads 'was blocked until'");
	}

	// Idempotence / no-clobber: an edge re-created deliberately AFTER the blocker went terminal
	// must survive an unrelated edit of that same (still terminal) node. This is the concrete
	// failure mode of the tempting simplification "fire whenever the current status is terminal"
	// instead of "fire on ENTERING one" — that version silently re-closes an edge a human just
	// restored, on the next tag edit.
	[Fact]
	public async Task AlreadyTerminalBlocker_TouchedAgain_DoesNotReCloseAReinstatedEdge()
	{
		var (blockerId, depId) = await Pair("sboard", "simple", "Todo", "Blocked");
		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("blocker", status: "Done", version: 1) });
		(await ActiveBlocksInto(depId)).Should().BeEmpty();

		await _relations.CreateAsync(Proj, "blocks", blockerId, depId);
		(await ActiveBlocksInto(depId)).Should().ContainSingle("control: the edge really was reinstated");

		// An edit that does NOT change status (the node is already Done and stays Done).
		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("blocker", status: "Done", version: 2) });

		(await ActiveBlocksInto(depId)).Should().ContainSingle("the rule fires on ENTERING a terminal status, not on being in one");
	}

	// ---------------------------------------------------------------------------------------
	// The backfill migrator.
	// ---------------------------------------------------------------------------------------

	// A pre-existing stranded edge: written straight into the store so the engine rule never saw
	// the blocker go terminal — exactly the fleet state the backfill exists for.
	async Task<(string Blocker, string Dependent)> StrandedEdge()
	{
		var (blockerId, depId) = await Pair("sboard", "simple", "Todo", "Blocked");
		// The blocker goes terminal WITHOUT an upsert, so the engine rule never fired and the edge
		// is left stranded — precisely the pre-rule fleet state.
		ForceStatus(blockerId, "Done");
		(await ActiveBlocksInto(depId)).Should().ContainSingle("control: the fixture really is a stranded edge");
		return (blockerId, depId);
	}

	// RED if the migrator wrote anything in dry run: this is the assertion that stands between a
	// read-only rehearsal and an unannounced mutation of live project databases.
	[Fact]
	public async Task Backfill_DryRun_ReportsTheEdge_AndWritesNothing()
	{
		var (_, depId) = await StrandedEdge();

		var found = new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: false).Migrate();

		found.Should().Be(1, "the dry run must still COUNT and report what it would close");
		(await ActiveBlocksInto(depId)).Should().ContainSingle("dry run writes NOTHING");
		StatusOf(depId).Should().Be("Blocked");
	}

	[Fact]
	public async Task Backfill_Apply_ClosesTheEdge_AndIsIdempotent()
	{
		var (_, depId) = await StrandedEdge();

		var first = new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: true).Migrate();
		var second = new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: true).Migrate();

		first.Should().Be(1);
		second.Should().Be(0, "a closed edge is no longer active — a second pass finds nothing");
		(await ActiveBlocksInto(depId)).Should().BeEmpty();
		(await BlocksInto(depId, includeHistory: true)).Should().ContainSingle()
			.Which.ClosedAt.Should().NotBeNull("soft-closed, so the id in the log still resolves for a manual restore");
		StatusOf(depId).Should().Be("Blocked", "`simple` declares no BlocksGate — the backfill closes the edge, it does not invent a status move");
	}

	// The backfill must NOT touch an edge whose blocker is still open — the precondition is on the
	// EDGE, and getting it wrong here would mass-close live blocks across the fleet. This is the
	// single most expensive way this migrator could be wrong, so it gets its own test.
	[Fact]
	public async Task Backfill_LeavesEdgesWithANonTerminalBlockerAlone()
	{
		await _tasks.CreateBoardAsync(Proj, "sboard", "simple", null, null);
		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("open-blocker", status: "InProgress") });
		await _tasks.UpsertAsync(Proj, "sboard", new[] { Node("dep", status: "Blocked") });
		var depId = NodeId("sboard", "dep");
		await _relations.CreateAsync(Proj, "blocks", NodeId("sboard", "open-blocker"), depId);

		var found = new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: true).Migrate();

		found.Should().Be(0);
		(await ActiveBlocksInto(depId)).Should().ContainSingle("an open blocker's edge is untouched");
	}

	// The backfill releases a gated dependent through the SAME code the live rule uses. Red without
	// the rule's release half; also red if the backfill closed edges without ever releasing.
	[Fact]
	public async Task Backfill_ReleasesAGatedDependent_OnTheDependentsOwnBoard()
	{
		var (blockerId, depId) = await Pair("wboard", "work", "Pending", "Blocked", type: "chore");
		ForceStatus(blockerId, "Done");

		var found = new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: true).Migrate();

		found.Should().Be(1);
		(await ActiveBlocksInto(depId)).Should().BeEmpty();
		StatusOf(depId).Should().Be("InProgress", "work declares BlocksGate(Blocked -> InProgress)");
	}

	// THE ROLLBACK CONTRACT, pinned. `relations` has no "closed by" column, so the ONLY record of
	// what a backfill pass touched is its log. If a line does not carry the relation's primary key
	// and both endpoints, the pass cannot be undone by hand and — per the owner's decision on the
	// card — is not a backfill that may be run at all. This test fails if a future edit thins the
	// line out, which is otherwise the kind of change nobody notices until an undo is needed.
	[Fact]
	public async Task Backfill_DryRunLog_CarriesEverythingNeededForAManualRestore()
	{
		var (blockerId, depId) = await StrandedEdge();
		var edgeId = (await ActiveBlocksInto(depId)).Single().Id;
		var log = new CapturingLogger();

		new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: false, log).Migrate();

		var line = log.Lines.Single(l => l.Contains("closing edge="));
		line.Should().Contain("DRY-RUN", "the mode must be unmistakable in the log itself");
		line.Should().Contain($"edge={edgeId}", "the relation primary key IS the undo handle");
		line.Should().Contain($"from={blockerId}").And.Contain($"to={depId}", "both endpoints, so the edge is reconstructible even if the row were hard-deleted");
		line.Should().Contain("project=proj").And.Contain("sboard/blocker").And.Contain("sboard/dep");
		line.Should().Contain("predictedRelease=none", "and what the status side of the undo would be, if anything");
		log.Lines.Should().Contain(l => l.Contains("UPDATE relations SET ClosedAt = NULL"),
			"the undo procedure is printed next to the ids it applies to, not left in a commit message");
		log.Lines.Should().Contain(l => l.Contains("RELEASED line"),
			"the hint must send an operator to the RELEASED lines for the status half, not to the forecast field");

		// Emitted so a reviewer can read the actual rehearsal output rather than trust a summary.
		foreach (var l in log.Lines) Console.WriteLine(l);
	}

	// THE TWO-BLOCKER HOLE IN THE REGISTER. A dependent with TWO terminal blockers: the backfill
	// closes both edges, and the SECOND close really does free the node. The forecast used to be
	// computed per-edge from the start-of-pass snapshot ("this dependent has exactly one edge"),
	// so BOTH lines said no release while the status moved anyway — and the printed undo procedure
	// said to restore statuses only where a line reported one. An operator replaying that log
	// restores the edges and silently leaves the node released.
	//
	// RED before the fix on BOTH assertions: no RELEASED line was ever written (the live rule
	// logged closures only), and the forecast on the last line read `predictedRelease=none`.
	[Fact]
	public async Task Backfill_TwoTerminalBlockers_RecordsTheReleaseThatActuallyHappened()
	{
		await _tasks.CreateBoardAsync(Proj, "wboard", "work", null, null);
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("b1", status: "Pending", type: "chore"), Node("b2", status: "Pending", type: "chore") });
		var b1 = NodeId("wboard", "b1");
		var b2 = NodeId("wboard", "b2");
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("dep", blockedBy: b1, status: "Blocked", type: "chore") });
		var depId = NodeId("wboard", "dep");
		await _relations.CreateAsync(Proj, "blocks", b2, depId);
		// Both blockers reach a terminal status WITHOUT an upsert — the stranded fleet state.
		ForceStatus(b1, "Done");
		ForceStatus(b2, "Cancelled");
		(await ActiveBlocksInto(depId)).Should().HaveCount(2, "control: two live edges from two terminal blockers");
		var log = new CapturingLogger();

		new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: true, log).Migrate();

		StatusOf(depId).Should().Be("InProgress", "control: the pass really did move the status");
		var released = log.Lines.Where(l => l.Contains("RELEASED node=")).ToList();
		released.Should().ContainSingle("the status move happened exactly once and must be recorded exactly once");
		released[0].Should().Contain($"node={depId}").And.Contain("wboard/dep")
			.And.Contain("from=Blocked").And.Contain("to=InProgress",
				"the register must carry what to restore and on which node — this line IS the undo");

		// And the forecast on the closure lines must not contradict it: exactly one of the two
		// says a release is coming, and it is the last one.
		log.Lines.Count(l => l.Contains("closing edge=") && !l.Contains("predictedRelease=none"))
			.Should().Be(1, "the forecast is per-pass, not per-snapshot-edge");
	}

	// The same shape in DRY RUN: two terminal blockers, nothing written, and the forecast still
	// tells the truth about the status move the apply run would make. Red before the fix — the
	// snapshot form printed `none` on both lines.
	[Fact]
	public async Task Backfill_DryRun_TwoTerminalBlockers_ForecastsTheReleaseOnTheLastEdge()
	{
		await _tasks.CreateBoardAsync(Proj, "wboard", "work", null, null);
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("b1", status: "Pending", type: "chore"), Node("b2", status: "Pending", type: "chore") });
		var b1 = NodeId("wboard", "b1");
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("dep", blockedBy: b1, status: "Blocked", type: "chore") });
		var depId = NodeId("wboard", "dep");
		await _relations.CreateAsync(Proj, "blocks", NodeId("wboard", "b2"), depId);
		ForceStatus(b1, "Done");
		ForceStatus(NodeId("wboard", "b2"), "Done");
		var log = new CapturingLogger();

		new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: false, log).Migrate();

		(await ActiveBlocksInto(depId)).Should().HaveCount(2, "dry run writes NOTHING");
		StatusOf(depId).Should().Be("Blocked");
		log.Lines.Count(l => l.Contains("closing edge=") && l.Contains("predictedRelease=Blocked->InProgress"))
			.Should().Be(1, "the plan must show the status move it is going to make");
		log.Lines.Should().NotContain(l => l.Contains("RELEASED node="), "nothing was released — nothing happened");
	}

	// A dependent whose OTHER blocker is still open is not freed, and the forecast must say so.
	// This is the control side of the two tests above: the fix must not turn "all my blockers are
	// terminal" into "any of my blockers is terminal".
	[Fact]
	public async Task Backfill_OneTerminalOneOpenBlocker_ClosesOnlyOne_AndForecastsNoRelease()
	{
		await _tasks.CreateBoardAsync(Proj, "wboard", "work", null, null);
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("b1", status: "Pending", type: "chore"), Node("b2", status: "Pending", type: "chore") });
		var b1 = NodeId("wboard", "b1");
		var b2 = NodeId("wboard", "b2");
		await _tasks.UpsertAsync(Proj, "wboard", new[] { Node("dep", blockedBy: b1, status: "Blocked", type: "chore") });
		var depId = NodeId("wboard", "dep");
		await _relations.CreateAsync(Proj, "blocks", b2, depId);
		ForceStatus(b1, "Done"); // b2 stays open
		var log = new CapturingLogger();

		var found = new TerminalBlockerEdgeBackfillMigrator(_db.Factory(), _factory, apply: true, log).Migrate();

		found.Should().Be(1, "only the terminal blocker's edge qualifies");
		(await ActiveBlocksInto(depId)).Should().ContainSingle().Which.FromNodeId.Should().Be(b2);
		StatusOf(depId).Should().Be("Blocked", "a live blocker remains");
		log.Lines.Should().NotContain(l => l.Contains("RELEASED node="));
		log.Lines.Single(l => l.Contains("closing edge=")).Should().Contain("predictedRelease=none");
	}

	sealed class CapturingLogger : ILogger
	{
		public List<string> Lines { get; } = [];
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
		public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
		public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
	}

	// A declared kind with a BlocksGate under CUSTOM status names — so a gate lookup that reached
	// for the built-in presets instead of this document would find nothing.
	static MethodologyDefinition GatedDef(string name, string kindSlug) => new(name,
	[
		new MethodologyKindDef(kindSlug, QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(
				["task"],
				[
					new WorkflowStatus("Waiting", "Waiting", StatusKind.Open),
					new WorkflowStatus("Running", "Running", StatusKind.Open),
					new WorkflowStatus("Finished", "Finished", StatusKind.TerminalOk),
				],
				[
					new MethodologyTransitionDef("Waiting", "Running"),
					new MethodologyTransitionDef("Running", "Finished"),
					new MethodologyTransitionDef("Waiting", "Finished"),
				]),
		])
		{
			BlocksGate = new MethodologyBlocksGateDef("Waiting", "Running"),
		},
	]);
}
