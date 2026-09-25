using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Services.Scheduling;

namespace PetBox.Tests.Tasks;

// The daily alarm (idea recurring-run-scheduler; work node-snooze-and-wake-job + recurring-card-rules;
// spec subtree `scheduled-wake`). Organised by spec leaf:
//   * node-snooze-until           — a node carries a snooze; a snooze without a date is refused;
//   * snooze-wakes-without-a-human — the pass wakes a due node ONCE, leaves its status alone, and
//                                    drops (without waking) the snooze of a node that closed first;
//   * snooze-wake-addressee       — only an owner-addressed wake sets decisionPending;
//   * owner-digest-shows-woken    — the digest counts wakes and lists the owner's;
//   * recurring-card-rule         — a due rule creates its card, which names the rule;
//   * recurring-card-no-pileup    — an open previous card blocks a second one; the miss is counted.
// Time is a FakeTimeProvider started at the real clock (the temporal store stamps revisions with the
// real clock, and the digest window compares against both), then advanced by whole days.
public sealed class NodeSnoozeAndRecurringFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<TasksDb> Factory { get; }

	public NodeSnoozeAndRecurringFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-snooze-" + Guid.NewGuid().ToString("N"));
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

public sealed class NodeSnoozeAndRecurringTests : IClassFixture<NodeSnoozeAndRecurringFixture>
{
	const string Proj = NodeSnoozeAndRecurringFixture.Proj;
	const string Work = "work";

	readonly NodeSnoozeAndRecurringFixture _fx;
	readonly TasksService _tasks;
	readonly CommentService _comments;
	readonly TaskBoardStore _boards;
	readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
	readonly RecurringRuleService _rules;
	readonly ScheduledWakeService _schedule;

	public NodeSnoozeAndRecurringTests(NodeSnoozeAndRecurringFixture fx)
	{
		fx.Reset();
		_fx = fx;
		_boards = new TaskBoardStore(fx.Db.Factory(), fx.Factory);
		_comments = new CommentService(fx.Factory);
		_tasks = new TasksService(_boards, new RelationStore(fx.Factory), new TagStore(fx.Factory), _comments);
		_rules = new RecurringRuleService(_boards, _tasks, _comments, _time);
		_schedule = new ScheduledWakeService(fx.Db.Factory(), _boards, _tasks, _rules,
			NullLogger<ScheduledWakeService>.Instance, _time);
	}

	DateTime Now => NodeSnooze.Normalize(_time.GetUtcNow().UtcDateTime);

	async Task SeedAsync(params string[] keys)
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		var applied = await _tasks.UpsertAsync(Proj, Work, keys.Select(k => new NodePatch
		{
			Key = k,
			Type = "chore",
			Title = k,
			Body = "body of " + k,
		}).ToList());
		applied.Result.Applied.Should().BeTrue();
	}

	async Task<TaskNodeView> Read(string key) =>
		(await _tasks.GetAsync(Proj, Work, includeClosed: true)).Nodes.Single(n => n.Key == key);

	async Task<UpsertOutcome> Snooze(string key, NodeSnoozeEdit edit, string? status = null) =>
		await _tasks.UpsertAsync(Proj, Work,
			[new NodePatch { Key = key, Version = (await Read(key)).Version, Snooze = edit, Status = status }]);

	async Task<IReadOnlyList<string>> SearchKeys(bool? snoozed = null, bool? woke = null) =>
		(await _tasks.SearchNodesAsync(Proj, new PetBox.Core.Contract.SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: Work, Snoozed: snoozed, Woke: woke),
			Limit = 0,
			BodyLen = 0,
		})).Hits.Select(h => h.Node.Key).Order(StringComparer.Ordinal).ToList();

	// ── storage: the columns survive a write and a read (AGENTS.md hard invariant) ──────────────

	// A column present in the migration but lost by the mapping reads back as its default while the
	// write reports success — the scar the round-trip rule exists for. Written and read through the
	// raw TasksDb table, not the service, so this proves the MAPPING, not the service's projection.
	[Fact]
	public void SnoozeColumns_RoundTrip_InsertThenSelect()
	{
		using var db = _fx.Factory.NewEnsuredConnection(Proj);
		var until = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
		var woke = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc);
		db.Insert(new TaskNode
		{
			Board = Work,
			Key = "rt",
			NodeId = "rtid",
			Status = "Pending",
			Type = "chore",
			Name = "rt",
			Version = 1,
			ActiveFrom = 1,
			Created = Now,
			Updated = Now,
			SnoozeUntil = until,
			SnoozeReason = "when opencode 2.x ships",
			SnoozeWakeTo = NodeSnooze.Owner,
			WokeAt = woke,
		});

		var back = db.TaskNodes.Single(n => n.Key == "rt");
		back.SnoozeUntil.Should().Be(until);
		back.SnoozeReason.Should().Be("when opencode 2.x ships");
		back.SnoozeWakeTo.Should().Be(NodeSnooze.Owner);
		back.WokeAt.Should().Be(woke);
	}

	[Fact]
	public void RecurringRuleRow_RoundTrip_InsertThenSelect()
	{
		using var db = _fx.Factory.NewEnsuredConnection(Proj);
		var due = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
		db.Insert(new RecurringRule
		{
			Id = "audit",
			Board = Work,
			Type = "chore",
			Title = "Audit",
			Body = "b",
			Tags = "area:tasks\nconcern:process",
			Period = RecurringPeriod.Month,
			WakeTo = NodeSnooze.Agent,
			NextDueAt = due,
			LastFiredAt = due.AddMonths(-1),
			OpenNodeId = "nid",
			MissedCount = 3,
			LastError = "boom",
			CreatedAt = due.AddYears(-1),
			UpdatedAt = due,
		});

		var back = db.RecurringRules.Single(r => r.Id == "audit");
		back.Should().BeEquivalentTo(new RecurringRule
		{
			Id = "audit",
			Board = Work,
			Type = "chore",
			Title = "Audit",
			Body = "b",
			Tags = "area:tasks\nconcern:process",
			Period = RecurringPeriod.Month,
			WakeTo = NodeSnooze.Agent,
			NextDueAt = due,
			LastFiredAt = due.AddMonths(-1),
			OpenNodeId = "nid",
			MissedCount = 3,
			LastError = "boom",
			CreatedAt = due.AddYears(-1),
			UpdatedAt = due,
		});
	}

	// ── node-snooze-until ───────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Snooze_IsCarriedOnTheNode_DefaultsToTheAgent_AndIsAFilter()
	{
		await SeedAsync("later", "now");
		var until = Now.AddDays(3);
		(await Snooze("later", new NodeSnoozeEdit { Until = until, Reason = "after the release" })).Result.Applied.Should().BeTrue();

		var n = await Read("later");
		n.Snooze.Should().BeEquivalentTo(new NodeSnoozeView(until, "after the release", NodeSnooze.Agent, null));
		n.Status.Should().Be("Pending", "a snooze is not a status");
		(await SearchKeys(snoozed: true)).Should().Equal(["later"]);
		(await SearchKeys(snoozed: false)).Should().Equal(["now"]);
		(await Read("now")).Snooze.Should().BeNull();
	}

	// The spec's MUST NOT: the machine never evaluates the condition, so a snooze with no date is a
	// node that never surfaces. Refused through conflicts[], nothing written.
	[Fact]
	public async Task Snooze_WithoutADate_IsRefused_AndNothingIsWritten()
	{
		await SeedAsync("t");
		var before = await Read("t");

		var r = await Snooze("t", new NodeSnoozeEdit { Reason = "when X happens" });

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("until");
		(await Read("t")).Version.Should().Be(before.Version);
		(await Read("t")).Snooze.Should().BeNull();
	}

	[Fact]
	public async Task Snooze_WithAnUnknownAddressee_IsRefused()
	{
		await SeedAsync("t");
		var r = await Snooze("t", new NodeSnoozeEdit { Until = Now.AddDays(1), WakeTo = "boss" });
		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Single().Reason.Should().Contain("wakeTo");
	}

	[Fact]
	public async Task Snooze_OnAWriteThatClosesTheNode_IsRefused()
	{
		await SeedAsync("t");
		var r = await Snooze("t", new NodeSnoozeEdit { Until = Now.AddDays(1) }, status: "Cancelled");
		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Single().Reason.Should().Contain("terminal");
	}

	[Fact]
	public async Task Clear_RemovesTheSnooze_AndAnOmittedSnoozeInherits()
	{
		await SeedAsync("t");
		await Snooze("t", new NodeSnoozeEdit { Until = Now.AddDays(1) });
		await _tasks.UpsertAsync(Proj, Work, [new NodePatch { Key = "t", Version = (await Read("t")).Version, Title = "retitled" }]);
		(await Read("t")).Snooze.Should().NotBeNull("an edit that says nothing about the snooze keeps it");

		await Snooze("t", new NodeSnoozeEdit { Clear = true });
		(await Read("t")).Snooze.Should().BeNull();
	}

	// Closing through the ordinary door drops the snooze in the same revision (no wake later).
	[Fact]
	public async Task ClosingASnoozedNode_DropsTheSnooze_WithoutAWake()
	{
		await SeedAsync("t");
		await Snooze("t", new NodeSnoozeEdit { Until = Now.AddDays(1), WakeTo = NodeSnooze.Owner });
		await _tasks.UpsertAsync(Proj, Work, [new NodePatch { Key = "t", Version = (await Read("t")).Version, Status = "Cancelled" }]);

		var n = await Read("t");
		n.Snooze.Should().BeNull("the snooze is gone and there was no wake to mark");
		n.DecisionPending.Should().BeFalse();
	}

	// ── snooze-wakes-without-a-human + snooze-wake-addressee ─────────────────────────────────────

	[Fact]
	public async Task Pass_WakesADueNode_Once_WithoutTouchingStatus_AgentGetsNoFlag_OwnerDoes()
	{
		await SeedAsync("for-agent", "for-owner", "not-yet", "never");
		await Snooze("for-agent", new NodeSnoozeEdit { Until = Now.AddDays(1) });
		await Snooze("for-owner", new NodeSnoozeEdit { Until = Now.AddDays(1), WakeTo = NodeSnooze.Owner });
		await Snooze("not-yet", new NodeSnoozeEdit { Until = Now.AddDays(5) });

		// Before the date: nothing moves.
		var early = await _schedule.RunProjectAsync(Proj);
		early.Woken.Should().BeEmpty();

		_time.Advance(TimeSpan.FromDays(2));
		var pass = await _schedule.RunProjectAsync(Proj);
		pass.Woken.Should().BeEquivalentTo(["for-agent", "for-owner"]);
		pass.WokenForOwner.Should().Equal(["for-owner"]);

		var agent = await Read("for-agent");
		agent.Snooze.Should().BeEquivalentTo(new NodeSnoozeView(null, "", NodeSnooze.Agent, Now));
		agent.Status.Should().Be("Pending", "a wake never changes the status");
		agent.DecisionPending.Should().BeFalse("an agent-addressed wake stays out of the owner's queue");
		(await Read("for-owner")).DecisionPending.Should().BeTrue("an owner-addressed wake lands in the owner's queue");
		(await Read("not-yet")).Snooze!.Until.Should().NotBeNull("its date has not come");
		(await Read("never")).Snooze.Should().BeNull();
		(await SearchKeys(woke: true)).Should().Equal(["for-agent", "for-owner"]);

		// Idempotent: the same day again writes nothing.
		var versions = (await _tasks.GetAsync(Proj, Work)).Nodes.ToDictionary(n => n.Key, n => n.Version);
		(await _schedule.RunProjectAsync(Proj)).Woken.Should().BeEmpty();
		(await _tasks.GetAsync(Proj, Work)).Nodes.ToDictionary(n => n.Key, n => n.Version).Should().Equal(versions);
	}

	// A snooze on a terminal node can only arrive through a door that bypasses the upsert's own
	// same-revision drop (a cascade, an older row). The pass drops it WITHOUT a wake and without a flag.
	[Fact]
	public async Task Pass_DropsTheSnoozeOfATerminalNode_WithoutWakingIt()
	{
		await SeedAsync("closed");
		await _tasks.UpsertAsync(Proj, Work, [new NodePatch { Key = "closed", Version = (await Read("closed")).Version, Status = "Cancelled" }]);
		using (var ctx = _fx.Factory.NewEnsuredConnection(Proj))
		{
			var row = ctx.TaskNodes.Single(n => n.Key == "closed" && n.ActiveTo == null);
			await TemporalStore.UpsertAsync(ctx, [row with { SnoozeUntil = Now.AddDays(-1), SnoozeWakeTo = NodeSnooze.Owner }],
				partition: n => n.Board == Work);
		}

		var pass = await _schedule.RunProjectAsync(Proj);

		pass.Woken.Should().BeEmpty();
		pass.ClearedOnTerminal.Should().Equal(["closed"]);
		var n = await Read("closed");
		n.Snooze.Should().BeNull("the snooze is gone and there was no wake to mark");
		n.DecisionPending.Should().BeFalse();
	}

	// ── owner-digest-shows-woken ────────────────────────────────────────────────────────────────

	[Fact]
	public async Task Digest_CountsWokenNodes_AndListsOnlyTheOwners()
	{
		await SeedAsync("a", "o");
		await Snooze("a", new NodeSnoozeEdit { Until = Now.AddMinutes(-1) });
		await Snooze("o", new NodeSnoozeEdit { Until = Now.AddMinutes(-1), WakeTo = NodeSnooze.Owner });
		await _schedule.RunProjectAsync(Proj);

		var digest = await new OwnerDigestService(_tasks, _comments, _time)
			.DigestAsync(Proj, new OwnerDigestRequest { Board = Work });

		digest.WokenTotal.Should().Be(2);
		digest.WokenForOwnerTotal.Should().Be(1);
		digest.WokenForOwner.Select(i => i.Key).Should().Equal(["o"]);
	}

	// ── recurring-card-rule + recurring-card-no-pileup ───────────────────────────────────────────

	[Fact]
	public async Task Rule_FiresACardThatNamesIt_AndNotTwiceTheSameDay()
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _rules.UpsertAsync(Proj, new RecurringRuleInput
		{
			Id = "audit",
			Board = Work,
			Type = "chore",
			Title = "Monthly audit",
			Body = "Run the playbook.",
			Period = RecurringPeriod.Month,
		});

		var first = await _schedule.RunProjectAsync(Proj);
		var key = first.Fired.Should().ContainKey("audit").WhoseValue;
		var card = await Read(key);
		card.Title.Should().Be("Monthly audit");
		card.Body.Should().Contain("Run the playbook.").And.Contain("recurring rule `audit`");
		card.DecisionPending.Should().BeFalse("an agent-addressed rule carries no owner flag");

		(await _schedule.RunProjectAsync(Proj)).Fired.Should().BeEmpty("the rule is not due again until next month");
		(await _tasks.GetAsync(Proj, Work)).Nodes.Should().ContainSingle();
	}

	// THE pile-up test: a month later, with the first card still open, there is still ONE card, the
	// rule counts the miss, and the open card says so.
	[Fact]
	public async Task Rule_WithItsPreviousCardOpen_CreatesNoSecondCard_AndCountsTheMiss()
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _rules.UpsertAsync(Proj, new RecurringRuleInput { Id = "hygiene", Board = Work, Type = "chore", Title = "Board hygiene", Period = RecurringPeriod.Day });
		var key = (await _schedule.RunProjectAsync(Proj)).Fired["hygiene"];

		_time.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(1)));
		var pass = await _schedule.RunProjectAsync(Proj);

		pass.Fired.Should().BeEmpty();
		pass.Skipped.Should().Equal(new Dictionary<string, long> { ["hygiene"] = 1 });
		(await _tasks.GetAsync(Proj, Work)).Nodes.Select(n => n.Key).Should().Equal([key]);
		(await _rules.ListAsync(Proj)).Single().MissedCount.Should().Be(1);
		var comments = await _comments.ListForNodeAsync(Proj, Work, (await Read(key)).NodeId);
		comments.Should().ContainSingle().Which.Body.Should().Contain("Missed periods so far: 1");

		// Once the card is closed, the next period fires a fresh one.
		await _tasks.UpsertAsync(Proj, Work, [new NodePatch { Key = key, Version = (await Read(key)).Version, Status = "Cancelled" }]);
		_time.Advance(TimeSpan.FromDays(1));
		var next = await _schedule.RunProjectAsync(Proj);
		next.Fired.Should().ContainKey("hygiene");
		(await _rules.ListAsync(Proj)).Single().MissedCount.Should().Be(0);
	}

	[Fact]
	public async Task OwnerAddressedRule_FlagsItsCard()
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _rules.UpsertAsync(Proj, new RecurringRuleInput { Id = "review", Board = Work, Type = "chore", Title = "Review", Period = RecurringPeriod.Week, WakeTo = NodeSnooze.Owner });
		var key = (await _schedule.RunProjectAsync(Proj)).Fired["review"];
		(await Read(key)).DecisionPending.Should().BeTrue();
	}

	// A template the board refuses is not forced in: the refusal is recorded on the rule, the
	// schedule does not advance (tomorrow retries), and nothing is created.
	[Fact]
	public async Task Rule_WhoseTemplateTheBoardRefuses_RecordsTheError()
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await _rules.UpsertAsync(Proj, new RecurringRuleInput { Id = "bad", Board = Work, Type = "no-such-type", Title = "Bad", Period = RecurringPeriod.Day });

		var pass = await _schedule.RunProjectAsync(Proj);

		pass.Failed.Should().ContainKey("bad");
		(await _tasks.GetAsync(Proj, Work)).Nodes.Should().BeEmpty();
		var rule = (await _rules.ListAsync(Proj)).Single();
		rule.LastError.Should().NotBeNullOrEmpty();
		rule.NextDueAt.Should().BeOnOrBefore(Now);
	}

	[Fact]
	public async Task RuleUpsert_RefusesAnUnknownBoard_AndAnUnknownPeriod()
	{
		await _tasks.CreateBoardAsync(Proj, Work, "work", null, null);
		await FluentActions.Invoking(() => _rules.UpsertAsync(Proj, new RecurringRuleInput { Id = "x", Board = "nope", Title = "X", Period = "day" }))
			.Should().ThrowAsync<ArgumentException>().WithMessage("*board*");
		await FluentActions.Invoking(() => _rules.UpsertAsync(Proj, new RecurringRuleInput { Id = "x", Board = Work, Title = "X", Period = "hourly" }))
			.Should().ThrowAsync<ArgumentException>().WithMessage("*period*");
	}

	[Fact]
	public void AdvancePast_MovesByWholePeriods_AndCountsThem()
	{
		var due = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		RecurringRuleService.AdvancePast(due, RecurringPeriod.Day, due.AddDays(3).AddHours(1))
			.Should().Be((due.AddDays(4), 4L));
		RecurringRuleService.AdvancePast(due, RecurringPeriod.Month, due)
			.Should().Be((due.AddMonths(1), 1L));
	}
}
