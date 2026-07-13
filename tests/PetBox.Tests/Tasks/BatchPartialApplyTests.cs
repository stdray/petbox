using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// atomic:false — the opt-in PARTIAL batch write (spec batch-write-partial-apply and its five
// leaves). The valid entries land, each refused entry comes back in conflicts[] with its own
// reason, and an entry that references a refused entry of the SAME call is refused too, so a
// partial write never leaves a dangling reference. WITHOUT the flag the batch stays
// all-or-nothing, bit for bit (spec batch-write-atomic-default).
public sealed class BatchPartialApplyTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public BatchPartialApplyTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-partial-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static NodePatch Node(string key, string? title = null, string? type = null, string? partOf = null,
		string? blockedBy = null, string? supersedes = null, long version = 0) => new()
		{
			Key = key,
			Title = title ?? key.ToUpperInvariant(),
			Body = "body of " + key,
			Type = type,
			PartOf = partOf,
			BlockedBy = blockedBy,
			Supersedes = supersedes,
			Version = version,
		};

	// A node the workflow refuses outright: `zzz` is not a legal type on a simple board.
	static NodePatch Bad(string key, string? partOf = null) => Node(key, type: "zzz", partOf: partOf);

	Task<UpsertOutcome> Upsert(string board, bool atomic, params NodePatch[] nodes) =>
		_tasks.UpsertAsync(Proj, board, nodes, actor: null, atomic: atomic);

	async Task<IReadOnlyList<string>> KeysOnBoardAsync(string board) =>
		(await _tasks.GetAsync(Proj, board)).Nodes.Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

	// ── the default: nothing changes without the flag ────────────────────────

	[Fact]
	public async Task WithoutTheFlag_OneInvalidNode_AbortsTheWholeBatch_NothingWritten()
	{
		// The historical contract: an invalid node fails the CALL — the valid siblings do not land.
		var act = () => _tasks.UpsertAsync(Proj, "b", new[] { Node("ok"), Bad("bad") });

		await act.Should().ThrowAsync<ArgumentException>();
		(await KeysOnBoardAsync("b")).Should().BeEmpty(); // not even the valid one
	}

	[Fact]
	public async Task WithoutTheFlag_StaleBaseline_AbortsTheWholeBatch()
	{
		await Upsert("b", atomic: true, Node("n"));                                  // v1
		await Upsert("b", atomic: true, Node("n", title: "moved", version: 1));      // -> v2 (another writer)

		// One stale node in the batch: the WHOLE call is refused, including the clean sibling.
		var r = await Upsert("b", atomic: true, Node("n", title: "mine", version: 1), Node("fresh"));

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Kind.Should().Be(TemporalConflictKind.Stale);
		r.Result.Added.Should().BeEmpty();
		(await KeysOnBoardAsync("b")).Should().Equal("n"); // `fresh` never landed
	}

	// ── partial: valid entries land, invalid ones are refused per entry ──────

	[Fact]
	public async Task Partial_ValidLands_InvalidRejectedWithItsOwnReason()
	{
		var r = await Upsert("b", atomic: false, Node("ok"), Bad("bad"), Node("ok2"));

		r.Result.Applied.Should().BeTrue();                                  // the batch DID write
		r.Result.Added.Select(n => n.Key).Should().BeEquivalentTo("ok", "ok2");
		var c = r.Result.Conflicts.Should().ContainSingle().Subject;
		c.Key.Should().Be("bad");
		c.Kind.Should().Be(TemporalConflictKind.Rejected);
		c.Reason.Should().Contain("invalid type 'zzz'");                     // the per-entry reason, verbatim

		(await KeysOnBoardAsync("b")).Should().Equal("ok", "ok2");           // the rejected node is NOT in the store
	}

	[Fact]
	public async Task Partial_EveryEntryRejected_NothingApplied()
	{
		var r = await Upsert("b", atomic: false, Bad("bad1"), Bad("bad2"));

		r.Result.Applied.Should().BeFalse();  // nothing survived — `applied` still means "something landed"
		r.Result.Conflicts.Select(c => c.Key).Should().BeEquivalentTo("bad1", "bad2");
		(await KeysOnBoardAsync("b")).Should().BeEmpty();
	}

	// ── cascade: a dependent of a rejected entry is rejected too ─────────────

	[Fact]
	public async Task Partial_Cascade_TransitiveDependentIsRejected_NoDanglingReference()
	{
		// bad <- child (partOf) <- grandchild (partOf): rejecting `bad` must take BOTH down,
		// or `grandchild` would hang off a parent that does not exist.
		var r = await Upsert("b", atomic: false,
			Bad("bad"),
			Node("child", partOf: "bad"),
			Node("grandchild", partOf: "child"),
			Node("unrelated"));

		r.Result.Applied.Should().BeTrue();
		r.Result.Added.Select(n => n.Key).Should().Equal("unrelated");
		(await KeysOnBoardAsync("b")).Should().Equal("unrelated");

		var byKey = r.Result.Conflicts.ToDictionary(c => c.Key, c => c);
		byKey.Keys.Should().BeEquivalentTo("bad", "child", "grandchild");
		byKey["bad"].Reason.Should().Contain("invalid type");                    // the PRIMARY reason
		byKey["child"].Reason.Should().Contain("depends on 'bad'");              // the CASCADE reason — distinguishable
		byKey["grandchild"].Reason.Should().Contain("depends on 'child'");       // transitive
	}

	[Fact]
	public async Task Partial_Cascade_FollowsBlockedByAndSupersedes_NotOnlyPartOf()
	{
		await Upsert("b", atomic: true, Node("victim")); // exists, so `supersedes` has a target

		var r = await Upsert("b", atomic: false,
			Bad("bad"),
			Node("blocked", blockedBy: "bad"),      // a blocker that never landed
			Node("replacer", supersedes: "bad"),    // replaces a node that never landed
			Node("fine", supersedes: "victim"));    // supersedes a node that DOES exist — untouched

		r.Result.Conflicts.Select(c => c.Key).Should().BeEquivalentTo("bad", "blocked", "replacer");
		r.Result.Conflicts.Where(c => c.Key != "bad")
			.Should().OnlyContain(c => c.Reason!.Contains("depends on 'bad'"));
		r.Result.Added.Select(n => n.Key).Should().Contain("fine");
		(await KeysOnBoardAsync("b")).Should().Contain("fine").And.NotContain("blocked");
	}

	[Fact]
	public async Task Partial_ReferenceCycle_DoesNotHang_AndRejectsTheWholeCycleWhenItTouchesARejection()
	{
		// a <-> b reference each other AND b hangs off a rejected node: the cycle must collapse,
		// not spin. A clean cycle (c <-> d) is left alone.
		var r = await Upsert("b", atomic: false,
			Bad("bad"),
			Node("a", blockedBy: "b2"),
			Node("b2", partOf: "bad", supersedes: "a"),
			Node("c", blockedBy: "d"),
			Node("d", blockedBy: "c"));

		r.Result.Conflicts.Select(c => c.Key).Should().BeEquivalentTo("bad", "a", "b2");
		// The clean cycle is not a rejection reason on its own — both members land.
		r.Result.Added.Select(n => n.Key).Should().BeEquivalentTo("c", "d");
	}

	// ── stale is a per-entry refusal, and it cascades like any other ─────────

	[Fact]
	public async Task Partial_StaleBaseline_RejectsOnlyThatEntry_AndCascadesToItsDependents()
	{
		await Upsert("b", atomic: true, Node("parent"));                                 // v1
		await Upsert("b", atomic: true, Node("parent", title: "moved", version: 1));     // -> v2, another writer

		// The author still holds v1 for `parent` (a GENUINE stale: the payload moved), and the
		// same call creates a child of it plus an unrelated node.
		var r = await Upsert("b", atomic: false,
			Node("parent", title: "mine", version: 1),
			Node("child", partOf: "parent"),
			Node("solo"));

		r.Result.Applied.Should().BeTrue();
		r.Result.Added.Select(n => n.Key).Should().Equal("solo");    // the clean node landed

		var byKey = r.Result.Conflicts.ToDictionary(c => c.Key, c => c);
		byKey.Keys.Should().BeEquivalentTo("parent", "child");
		byKey["parent"].Kind.Should().Be(TemporalConflictKind.Stale);        // the watermark's own verdict, unchanged
		byKey["parent"].ChangedFields.Should().NotBeNull();                  // still an INFORMED stale
		byKey["child"].Kind.Should().Be(TemporalConflictKind.Rejected);
		byKey["child"].Reason.Should().Contain("depends on 'parent'");       // a DIFFERENT reason from the primary

		// The stale node keeps the other writer's revision — a partial apply never clobbers it.
		var parent = (await _tasks.GetAsync(Proj, "b")).Nodes.Single(n => n.Key == "parent");
		parent.Title.Should().Be("moved");
	}

	[Fact]
	public async Task Partial_AutoResolvedStaleBaseline_Applies_AndDoesNotCascade()
	{
		await Upsert("b", atomic: true, Node("p", title: "T"));                          // v1
																						 // A bookkeeping rewrite that leaves the payload as the author read it (A -> A):
																						 // re-submitting the identical payload at a fresh baseline bumps nothing, so build the
																						 // A -> B -> A shape instead.
		await Upsert("b", atomic: true, Node("p", title: "B", version: 1));              // v2
		await Upsert("b", atomic: true, Node("p", title: "T", version: 2));              // v3 — back to what the author read

		// Baseline 1 is behind v3, but the payload never semantically moved => AutoResolved,
		// NOT a conflict — so it neither rejects nor cascades to its child.
		var r = await Upsert("b", atomic: false,
			Node("p", title: "T2", version: 1),
			Node("kid", partOf: "p"));

		r.Result.Applied.Should().BeTrue();
		r.Result.Conflicts.Should().BeEmpty();
		r.Result.AutoResolved.Should().Contain("p");
		(await KeysOnBoardAsync("b")).Should().Equal("kid", "p");
	}

	// ── the ack ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task Partial_Echo_CarriesExactlyTheLandedNodes_NeverARejectedOne()
	{
		await Upsert("b", atomic: true, Node("existing"));

		var r = await Upsert("b", atomic: false,
			Node("existing", title: "edited", version: 1),
			Bad("bad"),
			Node("new-one"));

		r.Result.Added.Select(n => n.Key).Should().Equal("new-one");
		r.Result.Updated.Select(n => n.Key).Should().Equal("existing");
		r.Result.Added.Concat(r.Result.Updated).Select(n => n.Key).Should().NotContain("bad");
		r.Result.Inserted.Should().Be(2); // the edit re-inserts a revision; the rejected node does not
	}
}
