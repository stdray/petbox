using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Features;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// Card decision-pending-survives-closure: a node that reaches a TERMINAL status is waiting on
// nobody, so it must not keep carrying `decisionPending`.
//
// WHY IT MATTERS AND WHERE IT LEAKED: `tasks_search` in QUERY mode defaults its visibility to
// open+terminalok, so a Done node with a stale flag answers `decisionPending:true` forever. The
// LISTING default is open, which is why OwnerDigestService.AwaitingAsync never saw it (and why
// this suite does not touch the digest).
//
// TWO doors close a node and both are covered here, because a fix on only one leaves the defect
// alive on the other:
//   * TasksService.ApplyWorkflow — the ordinary status write (`tasks_upsert`);
//   * TaskTransitionEffects.SetActiveNodeStatusAsync — the FSM CASCADE, e.g. the work preset's
//     `On: Done, Link: issue_task` effect, which drives the reported intake node to `done`
//     without the write path ever seeing a patch for it.
//
// Terminality is asserted through the BOARD'S OWN FSM vocabulary, never a spelling: `work` closes
// as Done (terminalok) / Cancelled (terminalcancel), `intake` as done / wontfix / duplicate. Both
// terminal KINDS are covered — a cancelled node waits on nobody either.
public sealed class DecisionPendingClosureTests : IClassFixture<NodeDecisionFlagAndProvenanceFixture>
{
	const string Proj = NodeDecisionFlagAndProvenanceFixture.Proj;
	const string Work = "w";
	const string Intake = "i";

	readonly NodeDecisionFlagAndProvenanceFixture _fx;
	readonly TasksService _tasks;
	readonly RelationStore _relations;
	readonly TaskUsageRecorder _usage;
	readonly TaskUsageReader _usageReader;

	public DecisionPendingClosureTests(NodeDecisionFlagAndProvenanceFixture fx)
	{
		fx.Reset();
		_fx = fx;
		var boards = new TaskBoardStore(fx.Db.Factory(), fx.Factory);
		_relations = new RelationStore(fx.Factory);
		_tasks = new TasksService(boards, _relations, new TagStore(fx.Factory), new CommentService(fx.Factory), llm: null);
		_usage = new TaskUsageRecorder(fx.Factory, fx.Db.Factory());
		_usageReader = new TaskUsageReader(boards);
	}

	// ── helpers ──────────────────────────────────────────────────────────────────────────────

	async Task<TaskNodeView> Read(string board, string key) =>
		(await _tasks.GetAsync(Proj, board, includeClosed: true)).Nodes.Single(n => n.Key == key);

	// Move a node to `status`, always against its own current version (the transition is the
	// point of every test here — a stale baseline would fail for the wrong reason).
	async Task MoveAsync(string board, string key, string status, string? reason = null)
	{
		var cur = await Read(board, key);
		await _tasks.UpsertAsync(Proj, board, [new NodePatch
		{
			Key = key, Status = status, Reason = reason, Version = cur.Version,
		}]);
	}

	// How many temporal revisions this node has (every row of the SCD-2 history, active or
	// superseded) — the acceptance criterion "the reset mints no SECOND revision" is a count.
	int Revisions(string nodeId) =>
		_fx.Factory.NewEnsuredConnection(Proj).TaskNodes.Count(n => n.NodeId == nodeId);

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build());

	// The exact read the card names as the leak: QUERY mode (its statusKind default is
	// open+terminalok) narrowed to the decision queue.
	Task<TaskSearchResultView> QueryWaiting(string board, string q) =>
		TasksTools.SearchAsync(Http(), Flags(), _tasks, _usage, _usageReader, Proj,
			q: q, board: board, bodyLen: 0, includeUrl: false, decisionPending: true);

	async Task SeedWorkAsync(params string[] keys)
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _tasks.UpsertAsync(Proj, Work, keys.Select(k => new NodePatch
		{
			Key = k,
			Type = "chore",
			Title = k,
			Body = "kettle body of " + k,
			DecisionPending = true,
		}).ToList());
	}

	// ── the ordinary write door ──────────────────────────────────────────────────────────────

	// terminalOK on `work`. The search assertion is the defect itself: query mode keeps
	// terminalok visible, so before the fix this Done node came back from the decision queue.
	[Fact]
	public async Task Done_ClearsTheFlag_AndTheDecisionQueueQueryStopsReturningTheNode()
	{
		await SeedWorkAsync("closing", "still-open");
		(await QueryWaiting(Work, "kettle")).Nodes.Select(n => n.Key)
			.Should().BeEquivalentTo(["closing", "still-open"], "both are open and flagged");

		await MoveAsync(Work, "closing", "InProgress");
		await MoveAsync(Work, "closing", "Review");
		await MoveAsync(Work, "closing", "Done");

		// The leak surface FIRST, deliberately: this is the read the card measured, so a
		// regression reports itself as "the closed node is back in the decision queue".
		(await QueryWaiting(Work, "kettle")).Nodes.Select(n => n.Key).Should().Equal(["still-open"],
			"query mode defaults to open+terminalok, so a stale flag would keep the closed node in the queue");
		(await Read(Work, "closing")).DecisionPending.Should().BeFalse(
			"a Done node waits on nobody — the flag must not survive the closure");
	}

	// terminalCANCEL on the same board: the second terminal KIND, which a fix written against
	// "Done" alone would miss.
	[Fact]
	public async Task Cancelled_ClearsTheFlagToo_TheOtherTerminalKind()
	{
		await SeedWorkAsync("dropping");

		await MoveAsync(Work, "dropping", "Cancelled");

		(await Read(Work, "dropping")).DecisionPending.Should().BeFalse(
			"terminalcancel is terminal too — a cancelled node is not waiting on a decision");
	}

	// The FSM, not the spelling: `intake` closes with three lowercase statuses across BOTH
	// terminal kinds, and none of them is called "Done".
	[Theory]
	[InlineData("done")]      // terminalok
	[InlineData("wontfix")]   // terminalcancel, RequiresReason
	[InlineData("duplicate")] // terminalcancel, RequiresReason
	public async Task EveryIntakeTerminalStatus_ClearsTheFlag_WhateverItIsCalled(string terminal)
	{
		await _tasks.CreateBoardAsync(Proj, Intake, "intake", null, null);
		await _tasks.UpsertAsync(Proj, Intake, [new NodePatch
		{
			Key = "issue", Type = "issue", Title = "issue", Body = "reported thing", DecisionPending = true,
		}]);

		await MoveAsync(Intake, "issue", "triage");
		if (terminal == "done")
		{
			await MoveAsync(Intake, "issue", "confirmed");
			await MoveAsync(Intake, "issue", "done");
		}
		else
		{
			await MoveAsync(Intake, "issue", terminal, reason: "closed in the test");
		}

		(await Read(Intake, "issue")).Status.Should().Be(terminal);
		(await Read(Intake, "issue")).DecisionPending.Should().BeFalse(
			"terminality comes from the board's own FSM — intake spells it done/wontfix/duplicate");
	}

	// THE OTHER DIRECTION, and the reason the reset is conditioned on terminality rather than on
	// "the status changed": a node moving BACK into the flow is still waiting on its decision.
	[Fact]
	public async Task ANonTerminalTransition_LeavesTheFlagAlone()
	{
		await SeedWorkAsync("bouncing");
		await MoveAsync(Work, "bouncing", "InProgress");
		await MoveAsync(Work, "bouncing", "Review");

		await MoveAsync(Work, "bouncing", "InProgress");

		(await Read(Work, "bouncing")).DecisionPending.Should().BeTrue(
			"Review -> InProgress is not a closure — an open node keeps waiting");
	}

	// The reset RIDES the revision the status change already mints; it must not add a second one.
	// Measured against a control node that closes WITHOUT the flag, so the assertion is "the same
	// number of revisions as an ordinary closure", not a hardcoded guess about the temporal store.
	[Fact]
	public async Task TheReset_RidesTheStatusRevision_AndMintsNoSecondOne()
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _tasks.UpsertAsync(Proj, Work, [
			new NodePatch { Key = "flagged", Type = "chore", Title = "flagged", Body = "b", DecisionPending = true },
			new NodePatch { Key = "control", Type = "chore", Title = "control", Body = "b" },
		]);
		var flaggedId = (await Read(Work, "flagged")).NodeId;
		var controlId = (await Read(Work, "control")).NodeId;
		var before = (Flagged: Revisions(flaggedId), Control: Revisions(controlId));

		await MoveAsync(Work, "flagged", "Cancelled");
		await MoveAsync(Work, "control", "Cancelled");

		var flaggedDelta = Revisions(flaggedId) - before.Flagged;
		var controlDelta = Revisions(controlId) - before.Control;
		flaggedDelta.Should().Be(controlDelta,
			"clearing the flag happens IN the status write — a closure with a flag costs exactly what a closure without one costs");
		flaggedDelta.Should().Be(1, "one closure, one revision");
	}

	// ── the cascade door ─────────────────────────────────────────────────────────────────────

	// The work preset's `On: Done, Link: issue_task, Direction: incoming, Set: done` effect closes
	// the intake issue that spawned the task. That intake node never passes through the write
	// path's workflow step, so it needs the reset at the cascade's own write — otherwise the
	// defect survives on exactly the closure nobody typed by hand.
	[Fact]
	public async Task TheIssueTaskCascade_ClearsTheFlag_OnTheIntakeNodeItCloses()
	{
		await _tasks.CreateBoardAsync(Proj, Intake, "intake", null, null);
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _tasks.UpsertAsync(Proj, Intake, [new NodePatch
		{
			Key = "issue", Type = "issue", Title = "issue", Body = "reported thing", DecisionPending = true,
		}]);
		await _tasks.UpsertAsync(Proj, Work, [new NodePatch
		{
			Key = "task", Type = "chore", Title = "task", Body = "b",
		}]);
		await _relations.CreateAsync(Proj, "issue_task",
			(await Read(Intake, "issue")).NodeId, (await Read(Work, "task")).NodeId);

		await MoveAsync(Work, "task", "InProgress");
		await MoveAsync(Work, "task", "Review");
		await MoveAsync(Work, "task", "Done");

		var issue = await Read(Intake, "issue");
		issue.Status.Should().Be("done", "the issue_task effect closes the reporting issue");
		issue.DecisionPending.Should().BeFalse(
			"the cascade closes the node just as truly as a typed transition does");
	}
}
