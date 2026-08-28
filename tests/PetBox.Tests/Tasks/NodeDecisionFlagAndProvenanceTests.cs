using System.Diagnostics;
using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
// `LogLevel` is ambiguous in this assembly (Microsoft.Extensions.Logging vs PetBox.Log.Core.Models
// — the log tier has its own). Alias it so the intent is stated once, at the top, instead of being
// spelled out at every use site.
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Observability;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// owner-decision-pending-flag + node-origin-provenance (card node-decision-flag-and-provenance-fields).
//
// Two spec leaves, deliberately two DIFFERENT storage shapes, and the tests are organised around
// the reason for the difference:
//   * the owner-decision-pending flag is a FIELD of the node — orthogonal to Status (a node can be
//     InProgress AND waiting), part of the payload (so a flip is a real revision the owner digest
//     can catch on tasks_delta), and queryable as a FILTER in both read modes;
//   * provenance is split — a WRITE-ONCE field for "which session created this" and an
//     ASSOCIATION for "which sessions have since touched it", the latter by the `Commits`
//     precedent so that accumulating it never mints a node revision.
//
// The session id is supplied EXPLICITLY by the caller. There is no server-side source for it (the
// MCP transport id is empty on effectively every real call), so a caller that forgets it produces
// a node with no origin — legal, but detected: a warning naming board+key and a per-call count on
// the tasks_upsert span.
public sealed class NodeDecisionFlagAndProvenanceFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<TasksDb> Factory { get; }

	public NodeDecisionFlagAndProvenanceFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-decisionflag-" + Guid.NewGuid().ToString("N"));
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

// Captures what the service actually logged, so the missing-sid detector is asserted on its real
// output (level + message + the board/key arguments) rather than on the fact that some code ran.
sealed class CapturingLogger<T> : ILogger<T>
{
	public List<(MsLogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
	public bool IsEnabled(MsLogLevel logLevel) => true;

	public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
		Entries.Add((logLevel, formatter(state, exception), pairs));
	}
}

public sealed class NodeDecisionFlagAndProvenanceTests : IClassFixture<NodeDecisionFlagAndProvenanceFixture>
{
	const string Proj = NodeDecisionFlagAndProvenanceFixture.Proj;
	const string Board = "b";

	readonly NodeDecisionFlagAndProvenanceFixture _fx;
	readonly TasksService _tasks;
	// M022 (task-usage-layer-declared-role) made the usage recorder/reader required collaborators
	// of SearchAsync. This suite asserts nothing about usage — it needs them only to call the tool.
	readonly TaskUsageRecorder _usage;
	readonly TaskUsageReader _usageReader;
	readonly CapturingLogger<TasksService> _log = new();

	public NodeDecisionFlagAndProvenanceTests(NodeDecisionFlagAndProvenanceFixture fx)
	{
		fx.Reset();
		_fx = fx;
		var boards = new TaskBoardStore(fx.Db.Factory(), fx.Factory);
		_tasks = new TasksService(boards,
			new RelationStore(fx.Factory), new TagStore(fx.Factory), new CommentService(fx.Factory),
			llm: null, log: _log);
		_usage = new TaskUsageRecorder(fx.Factory, fx.Db.Factory());
		_usageReader = new TaskUsageReader(boards);
	}

	// ── helpers ──────────────────────────────────────────────────────────────────────────────

	static NodePatch Node(string key, bool? decisionPending = null, string? title = null,
		string? status = null, long version = 0) => new()
		{
			Key = key,
			Title = title ?? key,
			Body = "body of " + key,
			Status = status,
			DecisionPending = decisionPending,
			Version = version,
		};

	async Task<TaskNodeView> Read(string key) =>
		(await _tasks.GetAsync(Proj, Board)).Nodes.Single(n => n.Key == key);

	List<TaskNodeOriginSession> OriginRows(string nodeId) =>
		_fx.Factory.NewEnsuredConnection(Proj).TaskNodeOriginSessions.Where(o => o.NodeId == nodeId).ToList();

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

	Task<TaskSearchResultView> Search(string? q = null, bool? decisionPending = null,
		int? limit = null, string? cursor = null) =>
		TasksTools.SearchAsync(Http(), Flags(), _tasks, _usage, _usageReader, Proj,
			q: q, board: Board, bodyLen: 0, limit: limit, includeUrl: false,
			cursor: cursor, decisionPending: decisionPending);

	// ── the flag is a FIELD, orthogonal to status ────────────────────────────────────────────

	// The flag must be readable independently of status/type (the spec's own wording) — so a node
	// that is mid-flight carries BOTH its work phase and the fact that it waits on the owner. This
	// is the whole reason the flag is not a status: a `Deferred`-style column would have to choose.
	[Fact]
	public async Task Flag_IsIndependentOfStatus_ANodeCanBeInProgressAndWaiting()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("waiting", decisionPending: true)]);

		var n = await Read("waiting");
		n.DecisionPending.Should().BeTrue();
		n.Status.Should().NotBeNullOrEmpty("the flag must not have replaced or disturbed the work phase");
	}

	// The RED-PROOF test for TaskNode.SamePayload: the temporal layer decides "did anything change"
	// by comparing payloads, so a flag the comparer cannot see is a flip the store silently
	// discards as a no-op. Both directions are asserted — off→on and on→off — because a comparer
	// that only noticed one would still pass a single-direction test.
	[Fact]
	public async Task FlagFlip_IsARealPayloadChange_MintsARevision_InBothDirections()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t")]);
		var born = await Read("t");
		born.DecisionPending.Should().BeFalse("a new node starts not-waiting");

		await _tasks.UpsertAsync(Proj, Board, [Node("t", decisionPending: true, version: born.Version)]);
		var on = await Read("t");
		on.DecisionPending.Should().BeTrue();
		on.Version.Should().BeGreaterThan(born.Version, "the flip is a payload change, so it is a new revision");

		await _tasks.UpsertAsync(Proj, Board, [Node("t", decisionPending: false, version: on.Version)]);
		var off = await Read("t");
		off.DecisionPending.Should().BeFalse();
		off.Version.Should().BeGreaterThan(on.Version);
	}

	// PATCH semantics, same as every other scalar: omitted means inherit, never "reset to false".
	// Without this, any unrelated edit would silently clear the owner's queue.
	[Fact]
	public async Task Flag_OmittedFromAPatch_Inherits_AndAnIdenticalResubmitIsANoOp()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t", decisionPending: true)]);
		var v1 = (await Read("t")).Version;

		// A patch that changes the title and says nothing about the flag.
		await _tasks.UpsertAsync(Proj, Board, [Node("t", title: "renamed title", version: v1)]);
		(await Read("t")).DecisionPending.Should().BeTrue("an omitted field inherits — it does not clear");

		// Re-asserting the same value writes no revision (the payload has not moved).
		var v2 = (await Read("t")).Version;
		await _tasks.UpsertAsync(Proj, Board, [Node("t", decisionPending: true, title: "renamed title", version: v2)]);
		(await Read("t")).Version.Should().Be(v2, "an identical payload is a no-op, flag included");
	}

	// ── provenance: the write-once field ─────────────────────────────────────────────────────

	// "Write-once" has to mean write-once against the case that actually threatens it: a LATER
	// session editing the node. The node was not born in that session, so recording it would be a
	// wrong answer, not a late one.
	[Fact]
	public async Task OriginSessionId_IsWriteOnce_ALaterSessionDoesNotOverwriteIt()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t")], sessionId: "sess-alpha");
		var created = await Read("t");
		created.OriginSessionId.Should().Be("sess-alpha");

		await _tasks.UpsertAsync(Proj, Board, [Node("t", title: "edited", version: created.Version)],
			sessionId: "sess-beta");

		var edited = await Read("t");
		edited.OriginSessionId.Should().Be("sess-alpha", "the creating session is the origin, forever");
		edited.Title.Should().Be("edited", "the rest of the patch still applied");
	}

	// The other half of write-once: a node born WITHOUT a session must never acquire one later
	// from whichever session happened to edit it next. Emptiness here is a fact about the node.
	[Fact]
	public async Task OriginSessionId_BornEmpty_StaysEmpty_EvenWhenALaterCallCarriesASession()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t")]); // no sessionId at all
		var created = await Read("t");
		created.OriginSessionId.Should().BeEmpty();

		await _tasks.UpsertAsync(Proj, Board, [Node("t", title: "edited", version: created.Version)],
			sessionId: "sess-late");

		(await Read("t")).OriginSessionId.Should().BeEmpty(
			"back-filling would record a session the node was NOT created in");
		// …but the late session IS part of the node's touch history, which is what the
		// association is for.
		(await Read("t")).OriginSessions.Should().BeEquivalentTo(["sess-late"]);
	}

	// ── provenance: the accumulating association ─────────────────────────────────────────────

	// The card's own acceptance criterion, and the reason this half is an association rather than
	// a field: N sessions must not mean N node revisions.
	[Fact]
	public async Task Provenance_Accumulates_WithoutMintingANodeRevision()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t")], sessionId: "s1");
		var afterCreate = await Read("t");

		// A DIFFERENT session touches the node with an otherwise identical payload.
		await _tasks.UpsertAsync(Proj, Board, [Node("t", version: afterCreate.Version)], sessionId: "s2");
		var afterTouch = await Read("t");

		afterTouch.OriginSessions.Should().BeEquivalentTo(["s1", "s2"]);
		afterTouch.Version.Should().Be(afterCreate.Version,
			"growing the provenance union must not revise the node — the Commits precedent");
	}

	// The duplicate question, answered explicitly: one session touching a node twice is a UNION,
	// not a log. Asserted at the STORAGE level too, because "the view shows one entry" would also
	// be true of two rows that happen to de-dupe on read.
	[Fact]
	public async Task Provenance_IsAUnion_ARepeatTouchByTheSameSessionAddsNoRow()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t")], sessionId: "s1");
		var v1 = (await Read("t")).Version;
		await _tasks.UpsertAsync(Proj, Board, [Node("t", title: "e1", version: v1)], sessionId: "s1");
		var v2 = (await Read("t")).Version;
		await _tasks.UpsertAsync(Proj, Board, [Node("t", title: "e2", version: v2)], sessionId: "s1");

		var node = await Read("t");
		node.OriginSessions.Should().BeEquivalentTo(["s1"]);
		OriginRows(node.NodeId).Should().ContainSingle("(NodeId, SessionId) is the primary key — a second touch cannot become a second row");
		OriginRows(node.NodeId).Single().Board.Should().Be(Board);
	}

	// ── the missing-sid detector ─────────────────────────────────────────────────────────────

	// The decision was: WARN, do not refuse. So both halves are asserted — the node lands, AND the
	// omission is visible with enough information (board + key) to find the node afterwards.
	// A refusal here would have broken every existing caller at once, which is why it was rejected.
	[Fact]
	public async Task MissingSessionId_StillCreatesTheNode_ButWarnsWithBoardAndKey_AndCountsOnTheSpan()
	{
		// A board name unique to this test: an ActivityListener is PROCESS-global and the suite runs
		// test classes in parallel, so filtering only by operation name would also count another
		// class's session-less upsert (observed: {1, 2} instead of {2}).
		const string ownBoard = "detector-board";
		await _tasks.CreateBoardAsync(Proj, ownBoard, "simple", null, null);

		var counted = new List<int>();
		using var listener = new ActivityListener
		{
			ShouldListenTo = src => src.Name == PetBoxActivitySources.TasksSourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
			ActivityStopped = a =>
			{
				if (a.OperationName != "tasks_upsert") return;
				if (a.GetTagItem("petbox.board") as string != ownBoard) return;
				if (a.GetTagItem("petbox.origin_sid_missing") is int n) counted.Add(n);
			},
		};
		ActivitySource.AddActivityListener(listener);

		var outcome = await _tasks.UpsertAsync(Proj, ownBoard, [Node("orphan"), Node("orphan2")]);

		outcome.Result.Applied.Should().BeTrue("a missing session id must NEVER refuse the write");
		(await _tasks.GetAsync(Proj, ownBoard)).Nodes.Single(n => n.Key == "orphan")
			.OriginSessionId.Should().BeEmpty();

		var warnings = _log.Entries.Where(e => e.Level == MsLogLevel.Warning
			&& e.Message.Contains("no origin session", StringComparison.Ordinal)).ToList();
		warnings.Should().HaveCount(2, "one warning per node that was created without an origin");
		warnings.Select(w => w.State.Single(kv => kv.Key == "Key").Value?.ToString())
			.Should().BeEquivalentTo(["orphan", "orphan2"]);
		warnings.Select(w => w.State.Single(kv => kv.Key == "Board").Value?.ToString())
			.Should().AllBe(ownBoard);

		counted.Should().ContainSingle("one tasks_upsert call ran")
			.Which.Should().Be(2, "the span carries the per-call count beside petbox.conflicts");
	}

	// The detector must not cry wolf. An EDIT without a sid loses nothing (the origin is write-once
	// — already set, or permanently empty), so warning on every edit would bury the real signal.
	[Fact]
	public async Task MissingSessionId_OnAnEdit_DoesNotWarn_OnlyCreatesDo()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await _tasks.UpsertAsync(Proj, Board, [Node("t")], sessionId: "s1");
		_log.Entries.Clear();

		var v = (await Read("t")).Version;
		await _tasks.UpsertAsync(Proj, Board, [Node("t", title: "edited", version: v)]); // no sid

		_log.Entries.Where(e => e.Level == MsLogLevel.Warning).Should().BeEmpty(
			"an edit without a session cannot lose an origin that is already fixed");
	}

	// ── existing nodes (the backfill question) ───────────────────────────────────────────────

	// Nodes written before this card exist with the migration's defaults. That is the TRUE historic
	// value, not a hole: nothing has been marked pending yet and nothing recorded an origin. The
	// point being pinned is that such a node is EXCLUDED by decisionPending:true and INCLUDED by
	// decisionPending:false — a defaulted field must not read as "unknown" to the filter.
	[Fact]
	public async Task PreExistingNodes_ReadAsNotPendingWithNoOrigin_AndTheFilterTreatsThemAsFalse()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		// A node written the way every node was written before this card: no flag, no session.
		await _tasks.UpsertAsync(Proj, Board, [Node("legacy")]);

		var n = await Read("legacy");
		n.DecisionPending.Should().BeFalse();
		n.OriginSessionId.Should().BeEmpty();
		n.OriginSessions.Should().BeEmpty("[] — this projection looked and found none");

		(await Search(decisionPending: true)).Nodes.Should().BeEmpty();
		(await Search(decisionPending: false)).Nodes.Select(x => x.Key).Should().Equal("legacy");
	}

	// The IN-PLACE upgrade, which is what the live stand actually does — a file that already holds
	// nodes is ALTERed, not rebuilt from the current schema the way every other test's file is.
	// Hand-rolled at the M001 shape (the M011 migration test's own precedent) so the whole chain
	// M002..M021 runs over real pre-existing rows. What is being pinned: the node SURVIVES, and it
	// comes back with the migration's defaults rather than a read error or a lost row.
	[Fact]
	public void Migration_UpgradesAFileThatAlreadyHasNodes_WithoutTouchingTheirData()
	{
		var dir = Path.Combine(Path.GetTempPath(), "petbox-m021-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var cs = $"Data Source={Path.Combine(dir, "legacy.db")}";
		try
		{
			using (var c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
			{
				c.Open();
				using var cmd = c.CreateCommand();
				cmd.CommandText = """
					CREATE TABLE plan_nodes (
						Key TEXT NOT NULL, Version INTEGER NOT NULL, Status INTEGER NOT NULL,
						Name TEXT NOT NULL DEFAULT '', Body TEXT NOT NULL, CommitRef TEXT,
						Priority INTEGER NOT NULL DEFAULT 0, PrevKey TEXT,
						ActiveFrom INTEGER NOT NULL, ActiveTo INTEGER,
						Created TEXT NOT NULL, Updated TEXT NOT NULL, PRIMARY KEY (Key, Version));
					CREATE UNIQUE INDEX ux_plan_nodes_active_key ON plan_nodes (Key) WHERE ActiveTo IS NULL;
					CREATE TABLE VersionInfo (Version INTEGER NOT NULL, AppliedOn DATETIME, Description TEXT);
					INSERT INTO VersionInfo (Version, AppliedOn, Description) VALUES (1, '2026-01-01', 'M001');
					INSERT INTO plan_nodes (Key,Version,Status,Name,Body,CommitRef,Priority,ActiveFrom,ActiveTo,Created,Updated) VALUES
						('older','1',2,'Older','a body that predates the flag',NULL,7,1,NULL,'2026-01-01','2026-01-01');
					""";
				cmd.ExecuteNonQuery();
			}
			TestDirs.ClearPoolsUnder(dir);

			// The REAL Ensure, not TestSchema.Tasks: only FluentMigrator's VersionInfo-based
			// idempotency advances a file that exists but is behind, which is the case under test.
			TasksSchema.Ensure(cs);

			using var db = new TasksDb(TasksDb.CreateOptions(cs));
			var row = db.TaskNodes.Single(n => n.Key == "older" && n.ActiveTo == null);
			row.Body.Should().Be("a body that predates the flag", "the upgrade must not disturb existing data");
			row.Priority.Should().Be(7);
			row.DecisionPending.Should().BeFalse("nothing has ever been marked pending — that is the TRUE historic value");
			row.OriginSessionId.Should().BeEmpty("no origin was ever recorded for this node, and none is invented");
			db.TaskNodeOriginSessions.Count().Should().Be(0, "the association table exists and starts empty");
		}
		finally
		{
			TestDirs.CleanupOrDefer(dir);
		}
	}

	// ── the filter, in BOTH read modes and BOTH directions ───────────────────────────────────

	async Task SeedMixedBoardAsync()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, Board, McpInputs.NodesJson("""
			[{"key":"alpha-waiting","status":"Todo","title":"Alpha waiting","body":"alpha body","decisionPending":true},
			 {"key":"alpha-free","status":"Todo","title":"Alpha free","body":"alpha body"}]
			"""), sessionId: "sess-seed");
	}

	// The filter has to work in the LISTING (the owner digest's own read) and in QUERY mode (the
	// same predicate must not evaporate when a `q` is added), and it has to answer BOTH questions:
	// a filter that only ever narrowed on `true` would answer "what is NOT waiting" with everything.
	[Fact]
	public async Task DecisionPendingFilter_SelectsBothDirections_InListingAndInQueryMode()
	{
		await SeedMixedBoardAsync();

		// listing
		(await Search(decisionPending: true)).Nodes.Select(n => n.Key).Should().Equal("alpha-waiting");
		(await Search(decisionPending: false)).Nodes.Select(n => n.Key).Should().Equal("alpha-free");
		(await Search()).Nodes.Select(n => n.Key).Should().BeEquivalentTo(
			["alpha-waiting", "alpha-free"], "omitting the filter must not narrow anything");

		// query
		(await Search(q: "alpha")).Nodes.Should().HaveCount(2, "the lexical leg must match both seeded bodies");
		(await Search(q: "alpha", decisionPending: true)).Nodes.Select(n => n.Key).Should().Equal("alpha-waiting");
		(await Search(q: "alpha", decisionPending: false)).Nodes.Select(n => n.Key).Should().Equal("alpha-free");
	}

	// The lean cut, decided per field on the rule the `commits` exemption states: a field the tool
	// FILTERS on must survive (or a query selects rows by something the response then hides);
	// enrichment nothing selects on is exactly what the lean row exists to drop.
	[Fact]
	public async Task LeanCut_KeepsTheFlag_ButDropsProvenance_InQueryModeOnly()
	{
		await SeedMixedBoardAsync();

		var listed = (await Search()).Nodes.Single(n => n.Key == "alpha-waiting");
		listed.DecisionPending.Should().BeTrue();
		listed.OriginSessionId.Should().Be("sess-seed", "a listing row carries the full enrichment");
		listed.OriginSessions.Should().BeEquivalentTo(["sess-seed"]);

		var queried = (await Search(q: "alpha", decisionPending: true)).Nodes.Single();
		queried.Key.Should().Be("alpha-waiting");
		queried.DecisionPending.Should().BeTrue(
			"the flag is the axis this very row was selected on — hiding it is the friction the commits exemption ended");
		queried.OriginSessionId.Should().BeNull("provenance is enrichment: nothing selects on it, so it is lean-cut");
		queried.OriginSessions.Should().BeNull();
	}

	// A cursor is a promise about ONE ordered selection. decisionPending SELECTS rows, so a token
	// issued for the waiting set must not be honoured against the whole board — otherwise page 2
	// silently splices a different list onto page 1.
	[Fact]
	public async Task Cursor_IsRefused_WhenTheDecisionPendingFilterChangesMidWalk()
	{
		await _tasks.CreateBoardAsync(Proj, Board, "simple", null, null);
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, Board, McpInputs.NodesJson("""
			[{"key":"w1","status":"Todo","title":"W1","body":"x","decisionPending":true},
			 {"key":"w2","status":"Todo","title":"W2","body":"x","decisionPending":true},
			 {"key":"f1","status":"Todo","title":"F1","body":"x"}]
			"""));

		var page1 = await Search(decisionPending: true, limit: 1);
		page1.Nodes.Should().ContainSingle();
		page1.NextCursor.Should().NotBeNull("two nodes match, one was returned — the walk continues");

		var resumeSameFilter = async () => await Search(decisionPending: true, limit: 1, cursor: page1.NextCursor);
		await resumeSameFilter.Should().NotThrowAsync("the same question must keep paging");

		var resumeWithoutFilter = async () => await Search(limit: 1, cursor: page1.NextCursor);
		await resumeWithoutFilter.Should().ThrowAsync<Exception>(
			"dropping the filter is a DIFFERENT selection — the cursor must be refused, not re-pointed");
	}
}
