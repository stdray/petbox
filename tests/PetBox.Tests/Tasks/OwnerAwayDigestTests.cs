using System.Text.Json;
using LinqToDB;
using Microsoft.Extensions.Time.Testing;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// THE OWNER-AWAY DIGEST (spec `owner-away-digest`, work `owner-away-digest-delivery`).
//
// The properties under test are the ones a "looks right" review cannot check, and each is stated as
// the failure it prevents rather than as a feature:
//   * the SECTION ORDER is the product. It is fixed by the owner's decision (waiting → closed → new
//     cohorts → chronology) and is explicitly NOT chronological. A later editor who finds that order
//     illogical and "fixes" it breaks the deliverable while every functional test stays green — so
//     the order is asserted on the WIRE, where a field reshuffle in the record shows up.
//   * "waiting on you" must be a QUEUE, not a board dump: the flagged node in and the unflagged node
//     OUT, and empty when nothing waits. The reverse direction is the half that catches a section
//     which "works" by showing everything.
//   * that section is STATE, not change — clipping it to the absence period would hide exactly the
//     decisions that have waited longest.
//   * a closure is resolved from the board's own FSM, and its date is a PROXY that must be named as
//     one in the payload.
public sealed class OwnerAwayDigestFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<TasksDb> Factory { get; }

	public OwnerAwayDigestFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-ownerdigest-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		Factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
	}

	public void Reset()
	{
		Db.TaskBoards.Where(b => b.ProjectKey == Proj).Delete();
		using var tasks = Factory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(tasks);
	}

	public void Dispose()
	{
		Db.Dispose();
		Factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}
}

public sealed class OwnerAwayDigestTests : IClassFixture<OwnerAwayDigestFixture>
{
	const string Proj = OwnerAwayDigestFixture.Proj;
	const string Board = "b";

	readonly TasksService _tasks;
	readonly CommentService _comments;

	public OwnerAwayDigestTests(OwnerAwayDigestFixture fx)
	{
		fx.Reset();
		var boards = new TaskBoardStore(fx.Db.Factory(), fx.Factory);
		_comments = new CommentService(fx.Factory);
		_tasks = new TasksService(boards, new RelationStore(fx.Factory), new TagStore(fx.Factory), _comments);
	}

	// ── helpers ──────────────────────────────────────────────────────────────────────────────────

	OwnerDigestService Digest(TimeProvider? time = null) => new(_tasks, _comments, time);

	static NodePatch Node(string key, string? status = null, bool? decisionPending = null,
		IReadOnlyList<string>? tags = null, long version = 0) => new()
		{
			Key = key,
			Title = key.ToUpperInvariant(),
			Body = "body of " + key,
			Status = status,
			DecisionPending = decisionPending,
			Tags = tags,
			Version = version,
		};

	async Task Board_(params NodePatch[] nodes)
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		if (nodes.Length > 0) await _tasks.UpsertAsync(Proj, Board, nodes);
	}

	Task<OwnerDigestView> Run(OwnerDigestRequest? request = null, TimeProvider? time = null) =>
		Digest(time).DigestAsync(Proj, request ?? new OwnerDigestRequest { Board = Board });

	static IReadOnlyList<string> Keys(IEnumerable<OwnerDigestItem> items) => items.Select(i => i.Key).ToList();

	// The MCP door camelCases the same record, so the wire order this file asserts is the one an
	// agent actually receives.
	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

	// ── the section order IS the product ─────────────────────────────────────────────────────────

	// The owner's decision, asserted where it can actually break: the SERIALIZED order of the
	// response, which is the record's field order and is what both doors render from. A reshuffle of
	// the record — the single most plausible "cleanup" a future editor makes — fails here and only
	// here; every behavioural test in this file stays green through it.
	//
	// The order is NOT chronological on purpose: a feed is fine for a one-day absence and unreadable
	// for a two-week one, which is the whole reason this digest exists instead of a log.
	[Fact]
	public async Task SectionOrder_OnTheWire_IsByRequiredAction_NotChronological()
	{
		await Board_(Node("n1", decisionPending: true));

		var view = await Run(new OwnerDigestRequest { Board = Board, IncludeTimeline = true });
		var json = JsonSerializer.Serialize(view, CamelCase);

		var awaiting = json.IndexOf("\"awaitingDecision\"", StringComparison.Ordinal);
		var closed = json.IndexOf("\"closed\"", StringComparison.Ordinal);
		var cohorts = json.IndexOf("\"newCohorts\"", StringComparison.Ordinal);
		var timeline = json.IndexOf("\"timeline\"", StringComparison.Ordinal);

		awaiting.Should().BeGreaterThan(-1);
		closed.Should().BeGreaterThan(awaiting, "(1) waiting on your decision leads — the owner's fixed order");
		cohorts.Should().BeGreaterThan(closed, "(2) what closed comes before (3) new cohorts");
		timeline.Should().BeGreaterThan(cohorts, "(4) chronology is last, and only on request");
	}

	// ── (1) waiting on you: a QUEUE, both directions ─────────────────────────────────────────────

	// BOTH directions in one fact, because a section that simply shows the whole board passes the
	// forward half. The unflagged node exists, is open, is in the same period — and must be absent.
	[Fact]
	public async Task Awaiting_HoldsTheFlaggedNode_AndNotTheUnflagged()
	{
		await Board_(Node("waiting", decisionPending: true), Node("running"));

		var view = await Run();

		Keys(view.AwaitingDecision).Should().Contain("waiting");
		Keys(view.AwaitingDecision).Should().NotContain("running",
			"a node nobody flagged is not waiting on the owner — a section that showed it would be a board dump");
		view.AwaitingDecisionTotal.Should().Be(1);
	}

	// The empty case is a SEPARATE property: "no rows" must mean no rows, not "the filter was
	// skipped and everything came back".
	[Fact]
	public async Task Awaiting_IsEmptyWhenNothingWaits_NotTheWholeBoard()
	{
		await Board_(Node("a"), Node("b"), Node("c"));

		var view = await Run();

		view.AwaitingDecision.Should().BeEmpty("nothing carries the flag");
		view.AwaitingDecisionTotal.Should().Be(0);
		view.NewCohorts.Should().NotBeEmpty("the board is not empty — only the decision queue is");
	}

	// Section (1) is STATE, not change. Time is pushed a year past the board so the CHANGE sections
	// are provably empty; the decision queue must survive that, because a decision that waited
	// longer than the absence is more urgent, not less. A digest that clipped this section to its
	// own period would hide precisely the oldest debts.
	[Fact]
	public async Task Awaiting_SurvivesThePeriod_ItIsStateNotChange()
	{
		await Board_(Node("old-decision", decisionPending: true));

		var future = new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(365));
		var view = await Run(new OwnerDigestRequest { Board = Board, Days = 1 }, future);

		view.NewTotal.Should().Be(0, "the node predates the window by a year");
		view.ClosedTotal.Should().Be(0);
		Keys(view.AwaitingDecision).Should().Contain("old-decision",
			"the decision queue is state — clipping it to the absence period is what this assertion forbids");
	}

	// ── (2) what closed ──────────────────────────────────────────────────────────────────────────

	// Terminal is resolved from the BOARD'S OWN workflow (Done = TerminalOk, Cancelled =
	// TerminalCancel on the simple kind), never from how a status is spelled — and an open node in
	// the same window must stay out. The caveat travels in the payload so neither door can round it
	// off into a claim about cycle time.
	[Fact]
	public async Task Closed_HoldsTerminalNodesOnly_AndTheCaveatNamesTheDateAsAProxy()
	{
		await Board_(Node("shipped"), Node("dropped"), Node("still-open"));
		var board = await _tasks.GetAsync(Proj, Board);
		await _tasks.UpsertAsync(Proj, Board,
		[
			Node("shipped", status: "Done", version: board.Nodes.Single(n => n.Key == "shipped").Version),
			Node("dropped", status: "Cancelled", version: board.Nodes.Single(n => n.Key == "dropped").Version),
		]);

		var view = await Run();

		Keys(view.Closed).Should().BeEquivalentTo(["shipped", "dropped"]);
		Keys(view.Closed).Should().NotContain("still-open");
		view.Closed.Single(i => i.Key == "shipped").StatusKind.Should().Be("terminalok");
		view.Closed.Single(i => i.Key == "dropped").StatusKind.Should().Be("terminalcancel");
		view.ClosureDatingCaveat.Should().Contain("PROXY")
			.And.Contain("updatedAt", "the payload has to say what the date actually is, on every door");
	}

	// ── (3) new cohorts by theme ─────────────────────────────────────────────────────────────────

	// Grouping is the `area` tag axis the instance rules already declare — not a clustering invented
	// here. The no-area bucket is last however big it gets: it is a fallback, and letting it lead
	// buries the themes the grouping exists to surface.
	[Fact]
	public async Task NewCohorts_GroupOnTheAreaAxis_WithTheNoAreaBucketLast()
	{
		await Board_(
			Node("t1", tags: ["area:tasks"]),
			Node("t2", tags: ["area:tasks"]),
			Node("s1", tags: ["area:search"]),
			Node("u1"), Node("u2"), Node("u3"));

		var view = await Run();

		view.NewTotal.Should().Be(6);
		view.NewCohorts.Select(c => c.Area).Should().ContainInOrder("tasks", "search", OwnerDigestCohort.NoArea);
		view.NewCohorts[^1].Area.Should().Be(OwnerDigestCohort.NoArea,
			"the fallback bucket never leads, even when it is the biggest");
		view.NewCohorts.Single(c => c.Area == "tasks").Total.Should().Be(2);
		Keys(view.NewCohorts.Single(c => c.Area == "search").Items).Should().BeEquivalentTo(["s1"]);
	}

	// ── (4) chronology ───────────────────────────────────────────────────────────────────────────

	// Absent and empty are different answers: null means "you did not ask", which is what lets the
	// page say so instead of showing a silent void. When asked, the chronology carries the comments
	// (comments_delta) as well as the node revisions — the prose is the part of the absence a status
	// list cannot carry.
	[Fact]
	public async Task Timeline_IsNullUnlessAsked_AndCarriesCommentsWhenItIs()
	{
		await Board_(Node("n1"));
		var nodeId = (await _tasks.GetAsync(Proj, Board)).Nodes.Single().NodeId;
		await _comments.AddAsync(Proj, Board, nodeId, null, "someone", "a note left while you were away", null);

		var off = await Run();
		off.Timeline.Should().BeNull("chronology is opt-in — absent is not the same answer as empty");
		off.TimelineTotal.Should().BeNull();

		var on = await Run(new OwnerDigestRequest { Board = Board, IncludeTimeline = true });
		on.Timeline.Should().NotBeNull();
		on.Timeline!.Should().Contain(e => e.Kind == "comment" && e.Author == "someone");
		on.Timeline!.Should().Contain(e => e.Kind == "node" && e.NodeKey == "n1");
		on.CurrentCommentVersion.Should().BeGreaterThan(0, "the comment cursor to resume from is returned");
	}

	// ── the period ───────────────────────────────────────────────────────────────────────────────

	// A version cursor scopes the CHANGE sections to what happened after it, and leaves WindowStart
	// null: a cursor names a revision, not an instant, and printing a date for it would invent a
	// fact the cursor does not carry.
	[Fact]
	public async Task VersionCursor_ScopesThePeriod_AndLeavesWindowStartNull()
	{
		await Board_(Node("before"));
		var cursor = (await _tasks.GetAsync(Proj, Board)).CurrentVersion;
		await _tasks.UpsertAsync(Proj, Board, [Node("after")]);

		var view = await Run(new OwnerDigestRequest { Board = Board, SinceVersion = cursor });

		view.WindowStart.Should().BeNull("a version cursor is not an instant");
		view.SinceVersion.Should().Be(cursor);
		view.CurrentVersion.Should().BeGreaterThan(cursor, "the cursor to resume from moved");
		var newKeys = view.NewCohorts.SelectMany(c => Keys(c.Items)).ToList();
		newKeys.Should().Contain("after");
		newKeys.Should().NotContain("before", "the node that predates the cursor is not news");
	}
}
