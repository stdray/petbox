using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tests.Data;

// Exercises the generic temporal-upsert engine through a sample "plan node"
// payload (enum Status + Body + optional CommitRef). Ports the LINQPad
// concurrency scenarios into the repo with a temp-file SQLite DB.
[Collection("DataModule")]
public sealed class TemporalStoreTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public TemporalStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-temporal-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "plan.db")}";
		EnsureSchema(_cs);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Insert_NewNodes_AreActive_AtVersion1()
	{
		var r = await Upsert(
			Node("p16", PlanStatus.InProgress, "Phase 16"),
			Node("p16/w1", PlanStatus.Done, "Wave 1"));

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(2);
		(Active()).Should().HaveCount(2);
		(ActiveOf("p16"))!.Version.Should().Be(1);
	}

	[Fact]
	public async Task Edit_ClosesOld_InsertsNewRevision_KeepsHistory()
	{
		await Upsert(Node("wal", PlanStatus.Pending, "WAL"));                         // v1
		var r = await Upsert(Node("wal", PlanStatus.Done, "WAL done", baseline: 1));  // v2

		r.Applied.Should().BeTrue();
		r.Closed.Should().Be(1);
		r.Inserted.Should().Be(1);
		(ActiveOf("wal"))!.Status.Should().Be(PlanStatus.Done);
		(All()).Count(x => x.Key == "wal").Should().Be(2); // history preserved
	}

	[Fact]
	public async Task Resubmit_IdenticalPayload_IsNoOp()
	{
		await Upsert(Node("wal", PlanStatus.Done, "WAL"));                       // v1
		var r = await Upsert(Node("wal", PlanStatus.Done, "WAL", baseline: 1)); // identical

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(0);
		r.Closed.Should().Be(0);
		(All()).Count(x => x.Key == "wal").Should().Be(1);
	}

	[Fact]
	public async Task StaleEdit_Conflicts_AndDoesNotClobber()
	{
		await Upsert(Node("wal", PlanStatus.Pending, "WAL-v1"));                     // v1
		await Upsert(Node("wal", PlanStatus.Done, "WAL-by-B", baseline: 1));         // B -> v2

		// A still believes baseline is v1 (it read the plan before B committed).
		var r = await Upsert(Node("wal", PlanStatus.Done, "WAL-by-A", baseline: 1));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Key == "wal" && c.Kind == TemporalConflictKind.Stale);
		(ActiveOf("wal"))!.Body.Should().Be("WAL-by-B"); // B's change preserved
	}

	[Fact]
	public async Task IndependentNodes_DoNotConflict()
	{
		await Upsert(Node("p16", PlanStatus.InProgress, "Phase 16"));            // v1
		await Upsert(Node("p30", PlanStatus.Pending, "Phase 30"));              // B adds, v2

		var r = await Upsert(Node("p16", PlanStatus.Done, "Phase 16 done", baseline: 1));

		r.Applied.Should().BeTrue();
		(ActiveOf("p30"))!.Body.Should().Be("Phase 30"); // untouched, survives
	}

	// WATERMARK: a baseline above the scope cursor (here 5 vs an empty store's 0) is quoting a
	// version this scope never reached — a wrong-scope cursor, not an edit of a vanished node.
	// (Replaces the old "edit a vanished node -> Vanished": with a baseline <= the cursor there is
	// nothing to clobber, so a missing key re-creates; only a rename keeps a Vanished path.)
	[Fact]
	public async Task BaselineAboveScopeCursor_IsFutureBaseline()
	{
		var r = await Upsert(Node("ghost", PlanStatus.Done, "was here", baseline: 5));

		r.Applied.Should().BeFalse();
		var c = r.Conflicts.Should().ContainSingle(c => c.Key == "ghost").Subject;
		c.Kind.Should().Be(TemporalConflictKind.FutureBaseline);
		c.ActiveVersion.Should().Be(0);                     // the scope cursor it's ahead of
		c.Reason.Should().Contain("another board/scope");   // the two-version-space teaching message
	}

	[Fact]
	public async Task CloseRace_WhenBaselineClosedDuringApply_IsDetected()
	{
		await Upsert(Node("wal", PlanStatus.Pending, "WAL-v1")); // v1

		// A is about to edit wal@v1. The seam fires after A classifies but before
		// A's close: a concurrent writer advances wal to v2, so A's close finds no
		// active baseline row -> CloseRace (the in-window race, vs Stale before-read).
		var r = await UpsertRacing(
			Node("wal", PlanStatus.Done, "WAL-by-A", baseline: 1),
			onBeforeApply: () => Upsert(Node("wal", PlanStatus.Done, "WAL-by-B", baseline: 1)));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Kind == TemporalConflictKind.CloseRace);
		ActiveOf("wal")!.Body.Should().Be("WAL-by-B"); // B stands; A wrote nothing
	}

	[Fact]
	public async Task CommitRef_IsPartOfPayload_TriggersNewRevision()
	{
		await Upsert(Node("wal", PlanStatus.Done, "WAL"));                                       // v1, no commit
		var r = await Upsert(Node("wal", PlanStatus.Done, "WAL", baseline: 1, commit: "8b2e97d")); // only CommitRef changes

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(1);
		(ActiveOf("wal"))!.CommitRef.Should().Be("8b2e97d");
	}

	[Fact]
	public async Task Priority_ThenPath_DeterminesOrder()
	{
		await Upsert(
			Node("z", PlanStatus.Done, "Z", priority: 100),
			Node("a", PlanStatus.Done, "A", priority: 100),  // same priority -> path tiebreak
			Node("m", PlanStatus.Done, "M", priority: 50));

		Ordered().Select(x => x.Key).Should().Equal("m", "a", "z"); // pri 50 first; then a < z by path
	}

	[Fact]
	public async Task SparsePriority_InsertBetween_DoesNotTouchNeighbours()
	{
		await Upsert(
			Node("a", PlanStatus.Done, "A", priority: 100),
			Node("c", PlanStatus.Done, "C", priority: 300));

		// new node slots between via a gap priority — no renumbering of a/c
		var r = await Upsert(Node("b", PlanStatus.Pending, "B", priority: 200));

		r.Applied.Should().BeTrue();
		Ordered().Select(x => x.Key).Should().Equal("a", "b", "c");
		All().Count(x => x.Key == "a").Should().Be(1); // neighbours untouched: still one revision each
		All().Count(x => x.Key == "c").Should().Be(1);
	}

	[Fact]
	public async Task Rename_RetiresOldKey_CreatesNewWithLineage()
	{
		await Upsert(Node("old", PlanStatus.Done, "body"));                          // v1 at "old"
		var r = await Upsert(Node("new", PlanStatus.Done, "body", baseline: 1, prevKey: "old"));

		r.Applied.Should().BeTrue();
		ActiveOf("old").Should().BeNull();                  // old retired
		ActiveOf("new")!.PrevKey.Should().Be("old");        // lineage edge on the birth revision
	}

	[Fact]
	public async Task Rename_OntoOccupiedKey_Conflicts()
	{
		await Upsert(Node("old", PlanStatus.Done, "o"));    // v1
		await Upsert(Node("taken", PlanStatus.Done, "t"));  // v2

		var r = await Upsert(Node("taken", PlanStatus.Done, "x", baseline: 1, prevKey: "old"));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Kind == TemporalConflictKind.TargetOccupied);
		ActiveOf("old").Should().NotBeNull();               // both untouched
		ActiveOf("taken")!.Body.Should().Be("t");
	}

	[Fact]
	public async Task Delta_SinceCursor_ReturnsAdded_Updated_Removed()
	{
		var time = new SteppingTimeProvider(); // distinct, increasing per-batch timestamps
		await Upsert(0, time, Node("a", PlanStatus.Done, "A"));                              // v1
		await Upsert(0, time, Node("b", PlanStatus.Done, "B"));                              // v2
		await Upsert(0, time, Node("a", PlanStatus.InProgress, "A2", baseline: 1));          // v3: edit a
		await Upsert(0, time, Node("b2", PlanStatus.Done, "B", baseline: 2, prevKey: "b"));  // v4: rename b -> b2

		var r = await Upsert(0, time); // empty submit, just pull the delta since cursor 0

		r.Added.Select(x => x.Key).Should().BeEquivalentTo(["b2"]);   // never-edited new identity
		r.Updated.Select(x => x.Key).Should().BeEquivalentTo(["a"]);  // edited (Created carried)
		r.Removed.Should().BeEquivalentTo(["b"]);                     // renamed away, no active row
	}

	// ── WATERMARK baseline semantics ─────────────────────────────────────────
	// The baseline in Version is a watermark: the scope cursor the author read (or the
	// entity's own version). An edit is valid when baseline >= the entity's current version.

	[Fact]
	public async Task Edit_BaselineIsScopeCursor_AboveEntityVersion_Applies()
	{
		await Upsert(Node("a", PlanStatus.Done, "A"));   // v1
		await Upsert(Node("b", PlanStatus.Done, "B"));   // v2 -> cursor 2; a is still at v1

		// The author read the scope at cursor 2 and edits a with baseline 2 (> a's own v1).
		var r = await Upsert(Node("a", PlanStatus.InProgress, "A2", baseline: 2));

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(1);
		r.Closed.Should().Be(1);
		ActiveOf("a")!.Status.Should().Be(PlanStatus.InProgress);
		All().Count(x => x.Key == "a").Should().Be(2); // history preserved (closed v1 + new v3)
	}

	// THE trap this fixes: a batch stamped with a single scope cursor as every row's baseline —
	// under the old exact-match rule the untouched rows (whose own version < cursor) were Stale
	// and aborted the whole batch; the watermark lets them collapse to no-ops while the edit lands.
	[Fact]
	public async Task Batch_EditPlusUntouched_AllAtScopeCursorBaseline_Applies()
	{
		await Upsert(Node("a", PlanStatus.Done, "A"), Node("b", PlanStatus.Done, "B"), Node("c", PlanStatus.Done, "C")); // all v1
		await Upsert(Node("a", PlanStatus.InProgress, "A2", baseline: 1)); // v2 -> cursor 2; b,c still v1

		// Author read at cursor 2, resubmits all three at baseline 2, changing only b.
		var r = await Upsert(
			Node("a", PlanStatus.InProgress, "A2", baseline: 2), // identical to current -> no-op
			Node("b", PlanStatus.Done, "B-edited", baseline: 2), // real edit (b is v1 < 2) -> applies
			Node("c", PlanStatus.Done, "C", baseline: 2));       // identical to current -> no-op

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(1); // only b
		r.Closed.Should().Be(1);
		ActiveOf("b")!.Body.Should().Be("B-edited");
		ActiveOf("a")!.Body.Should().Be("A2"); // untouched, survives
		ActiveOf("c")!.Body.Should().Be("C");
	}

	[Fact]
	public async Task NewEntity_BaselineIsScopeCursor_OrZero_Creates()
	{
		await Upsert(Node("a", PlanStatus.Done, "A")); // v1 -> cursor 1

		// A brand-new key stamped with the scope cursor (author read, then added) creates.
		var withCursor = await Upsert(Node("fresh", PlanStatus.Pending, "F", baseline: 1));
		withCursor.Applied.Should().BeTrue();
		withCursor.Inserted.Should().Be(1);
		ActiveOf("fresh")!.Version.Should().Be(2);

		// baseline 0 still creates (unchanged classic path).
		var withZero = await Upsert(Node("fresh0", PlanStatus.Done, "F0"));
		withZero.Applied.Should().BeTrue();
		ActiveOf("fresh0").Should().NotBeNull();
	}

	// A payload identical to the CURRENT revision but on a STALE baseline is a conflict now —
	// SamePayload no longer short-circuits before the baseline is validated.
	[Fact]
	public async Task IdenticalPayload_StaleBaseline_Conflicts_NotSilentNoOp()
	{
		await Upsert(Node("wal", PlanStatus.Pending, "v1"));                 // v1
		await Upsert(Node("wal", PlanStatus.Done, "final", baseline: 1));    // B -> v2 (current = Done/final)

		// A resubmits the SAME payload as the current revision, but still believes baseline is v1.
		var r = await Upsert(Node("wal", PlanStatus.Done, "final", baseline: 1));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Key == "wal" && c.Kind == TemporalConflictKind.Stale);
	}

	// Identical payload on a VALID watermark baseline (cursor above the entity's own version) is
	// still a clean no-op — no new revision.
	[Fact]
	public async Task IdenticalPayload_WatermarkBaseline_IsNoOp()
	{
		await Upsert(Node("wal", PlanStatus.Done, "WAL")); // v1
		await Upsert(Node("other", PlanStatus.Done, "O")); // v2 -> cursor 2

		var r = await Upsert(Node("wal", PlanStatus.Done, "WAL", baseline: 2)); // identical, baseline = cursor 2 > wal's v1

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(0);
		All().Count(x => x.Key == "wal").Should().Be(1);
	}

	[Fact]
	public async Task Rename_BaselineIsScopeCursor_AboveSourceVersion_Applies()
	{
		await Upsert(Node("old", PlanStatus.Done, "body")); // v1
		await Upsert(Node("x", PlanStatus.Done, "X"));       // v2 -> cursor 2; old still v1

		var r = await Upsert(Node("new", PlanStatus.Done, "body", baseline: 2, prevKey: "old"));

		r.Applied.Should().BeTrue();
		ActiveOf("old").Should().BeNull();
		ActiveOf("new")!.PrevKey.Should().Be("old");
	}

	[Fact]
	public async Task Rename_SourceMovedPastBaseline_Conflicts()
	{
		await Upsert(Node("old", PlanStatus.Pending, "b1"));            // v1
		await Upsert(Node("old", PlanStatus.Done, "b2", baseline: 1));  // v2: source edited past baseline 1

		var r = await Upsert(Node("new", PlanStatus.Done, "b2", baseline: 1, prevKey: "old"));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Key == "old" && c.Kind == TemporalConflictKind.Stale);
		ActiveOf("old").Should().NotBeNull(); // untouched
	}

	// The one surviving Vanished path: a rename whose source key has no active row.
	[Fact]
	public async Task Rename_MissingSource_IsVanished()
	{
		var r = await Upsert(Node("new", PlanStatus.Done, "b", prevKey: "ghost"));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Key == "ghost" && c.Kind == TemporalConflictKind.Vanished);
	}

	[Fact]
	public async Task Delete_BaselineIsScopeCursor_AboveEntityVersion_Closes()
	{
		await Upsert(Node("a", PlanStatus.Done, "A")); // v1
		await Upsert(Node("b", PlanStatus.Done, "B")); // v2 -> cursor 2; a still v1

		var r = await Delete(("a", 2)); // baseline 2 (cursor) > a's own v1

		r.Applied.Should().BeTrue();
		r.Closed.Should().Be(1);
		ActiveOf("a").Should().BeNull();
	}

	[Fact]
	public async Task Delete_StaleBaseline_Conflicts()
	{
		await Upsert(Node("a", PlanStatus.Pending, "v1"));           // v1
		await Upsert(Node("a", PlanStatus.Done, "v2", baseline: 1)); // v2

		var r = await Delete(("a", 1)); // baseline 1 < current v2

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Key == "a" && c.Kind == TemporalConflictKind.Stale);
		ActiveOf("a").Should().NotBeNull();
	}

	[Fact]
	public async Task Delete_BaselineAboveCursor_IsFutureBaseline()
	{
		await Upsert(Node("a", PlanStatus.Done, "A")); // v1 -> cursor 1

		var r = await Delete(("a", 9)); // 9 > cursor 1

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Key == "a" && c.Kind == TemporalConflictKind.FutureBaseline);
		ActiveOf("a").Should().NotBeNull();
	}

	// ---- helpers ----
	static PlanRow Node(string key, PlanStatus status, string body,
		long baseline = 0, string? commit = null, long priority = 0, string? prevKey = null) =>
		new() { Key = key, Version = baseline, Status = status, Body = body, CommitRef = commit, Priority = priority, PrevKey = prevKey };

	List<PlanRow> Ordered()
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return db.GetTable<PlanRow>().Where(x => x.ActiveTo == null)
			.OrderBy(x => x.Priority).ThenBy(x => x.Key).ToList();
	}

	async Task<TemporalUpsertResult<PlanRow>> Upsert(params PlanRow[] rows)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return await TemporalStore.UpsertAsync(db, rows);
	}

	// Advances a shared clock and reads the delta from `since`.
	async Task<TemporalUpsertResult<PlanRow>> Upsert(long since, TimeProvider time, params PlanRow[] rows)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return await TemporalStore.UpsertAsync(db, rows, since, time: time);
	}

	// Soft-delete keys via the delete-list overload (no desired rows).
	async Task<TemporalUpsertResult<PlanRow>> Delete(params (string Key, long Version)[] dels)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return await TemporalStore.UpsertAsync(db, Array.Empty<PlanRow>(), dels);
	}

	// Drives the internal seam to reproduce CloseRace deterministically.
	async Task<TemporalUpsertResult<PlanRow>> UpsertRacing(PlanRow row, Func<Task> onBeforeApply)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return await TemporalStore.UpsertAsync(db, new[] { row }, [], 0, time: null, onBeforeApply: onBeforeApply, onWithinTx: null, partition: null, ct: CancellationToken.None);
	}

	List<PlanRow> Active()
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return db.GetTable<PlanRow>().Where(x => x.ActiveTo == null).OrderBy(x => x.Key).ToList();
	}

	List<PlanRow> All()
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return db.GetTable<PlanRow>().OrderBy(x => x.Key).ThenBy(x => x.Version).ToList();
	}

	PlanRow? ActiveOf(string key) => Active().FirstOrDefault(x => x.Key == key);

	static void EnsureSchema(string cs)
	{
		using var c = new SqliteConnection(cs);
		c.Open();
		using var cmd = c.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS PlanNode (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Status     INTEGER NOT NULL,
				Body       TEXT    NOT NULL,
				CommitRef  TEXT,
				Priority   INTEGER NOT NULL DEFAULT 0,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			""";
		cmd.ExecuteNonQuery();
	}
}

public enum PlanStatus { Pending, InProgress, Done, Blocked, Deferred, Cancelled }

// Sample domain payload: the extra columns (enum Status, CommitRef) ride along
// for free — the engine only calls SamePayload / AsRevision.
[Table("PlanNode")]
public sealed record PlanRow : TemporalRow
{
	[Column] public PlanStatus Status { get; init; }
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	[Column, Nullable] public string? CommitRef { get; init; }

	// Sparse ordering key: order is `Priority ASC, Key ASC`. Living as a per-node
	// payload field, a priority change goes through normal optimistic concurrency
	// and never renumbers siblings (unlike a dense Order column).
	[Column] public long Priority { get; init; }

	public override bool SamePayload(TemporalRow other) =>
		other is PlanRow p && p.Status == Status && p.Body == Body && p.CommitRef == CommitRef && p.Priority == Priority;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}

// Returns a distinct, strictly-increasing timestamp per call, so consecutive
// upserts get separable Created/Updated values regardless of wall-clock resolution.
sealed class SteppingTimeProvider : TimeProvider
{
	DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public override DateTimeOffset GetUtcNow()
	{
		var now = _now;
		_now = _now.AddSeconds(1);
		return now;
	}
}
