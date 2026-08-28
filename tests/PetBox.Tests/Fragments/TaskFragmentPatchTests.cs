using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Fragments;

// tasks_upsert `fragment` (work/write-fragment-patch): a point edit of the node body, resolved in
// the service's read-merge against the SAME prior row an omitted field inherits from, under the
// SAME version watermark as a full-body write.
//
// Every refusal here is asserted TWICE: once on the ack (applied:false + conflicts[]) and once on
// the STORE (the body read back is byte-identical to what it was). The second assertion is the one
// that matters — an ack that says "refused" while the write landed anyway is the failure mode a
// fragment feature can actually have, and only a read-back can see it.
public sealed class TaskFragmentPatchTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "b";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public TaskFragmentPatchTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-fragtask-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		var store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	const string Original = "## Intro\n\nfirst paragraph\n\n## Middle\n\nsecond paragraph\n\n## End\n\nthird paragraph";

	static FragmentEdit E(string? old, string? @new) => new(old, @new);

	async Task<long> SeedAsync(string key = "n", string body = Original)
	{
		var r = await _tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = key, Title = key, Body = body, Version = 0 }]);
		r.Result.Applied.Should().BeTrue();
		return (await NodeAsync(key)).Version;
	}

	async Task<TaskNodeView> NodeAsync(string key = "n") =>
		(await _tasks.GetAsync(Proj, Board)).Nodes.Single(n => n.Key == key);

	Task<UpsertOutcome> UpsertAsync(params NodePatch[] nodes) =>
		_tasks.UpsertAsync(Proj, Board, nodes);

	// ── CONTROL: the feature actually works ──────────────────────────────────────────
	// Without this, every refusal assertion below would still pass if `fragment` refused
	// unconditionally — "the write was refused" is not evidence of a correct guard.

	[Fact]
	public async Task UniqueFragment_PatchesOnlyThatSpan_AndBumpsTheVersion()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new NodePatch { Key = "n", Version = v, Fragment = [E("second paragraph", "SECOND PARAGRAPH")] });

		r.Result.Applied.Should().BeTrue();
		var n = await NodeAsync();
		n.Body.Should().Be(Original.Replace("second paragraph", "SECOND PARAGRAPH", StringComparison.Ordinal));
		n.Version.Should().BeGreaterThan(v);
	}

	[Fact]
	public async Task Fragment_LeavesEveryOtherFieldAlone_LikeAnyOtherPatch()
	{
		// A fragment write is an ordinary PATCH in every other respect: the title it does not
		// mention must survive, exactly as it does when `body` is omitted.
		var seed = await _tasks.UpsertAsync(Proj, Board,
			[new NodePatch { Key = "n", Title = "Keep This Title", Body = Original, Priority = 42, Version = 0 }]);
		seed.Result.Applied.Should().BeTrue();
		var v = (await NodeAsync()).Version;

		await UpsertAsync(new NodePatch { Key = "n", Version = v, Fragment = [E("first paragraph", "1st")] });

		var n = await NodeAsync();
		n.Title.Should().Be("Keep This Title");
		n.Priority.Should().Be(42);
		n.Body.Should().Contain("1st").And.NotContain("first paragraph");
	}

	// ── the card's central requirement: ambiguity is a refusal ───────────────────────

	[Fact]
	public async Task FragmentMatchingTwice_IsRefused_AndTheStoredBodyIsUnchanged()
	{
		var v = await SeedAsync(body: "repeat ME here and repeat ME there");

		var r = await UpsertAsync(new NodePatch { Key = "n", Version = v, Fragment = [E("repeat ME", "X")] });

		r.Result.Applied.Should().BeFalse();
		var c = r.Result.Conflicts.Should().ContainSingle().Subject;
		c.Key.Should().Be("n");
		c.Kind.Should().Be(TemporalConflictKind.Rejected);          // the SAME channel a stale baseline uses
		c.Reason.Should().Contain("occurs 2 times").And.Contain("EXACTLY once");

		// The point of the whole test: nothing was written. Not the first match, not anything.
		var n = await NodeAsync();
		n.Body.Should().Be("repeat ME here and repeat ME there");
		n.Version.Should().Be(v);
	}

	[Fact]
	public async Task FragmentMatchingZeroTimes_IsRefused_AndTheStoredBodyIsUnchanged()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new NodePatch { Key = "n", Version = v, Fragment = [E("this text is not there", "X")] });

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("does not occur");

		var n = await NodeAsync();
		n.Body.Should().Be(Original);
		n.Version.Should().Be(v);
	}

	// ── the list is all-or-none ──────────────────────────────────────────────────────

	[Fact]
	public async Task MultiEditList_AppliesEveryEdit_WhenAllOfThemMatch()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new NodePatch
		{
			Key = "n",
			Version = v,
			Fragment = [E("first paragraph", "1st"), E("second paragraph", "2nd"), E("third paragraph", "3rd")],
		});

		r.Result.Applied.Should().BeTrue();
		var body = (await NodeAsync()).Body;
		body.Should().Contain("1st").And.Contain("2nd").And.Contain("3rd");
		body.Should().NotContain("paragraph");   // every one of the three really landed
	}

	[Fact]
	public async Task MultiEditList_WithOneBadEdit_AppliesNONEOfThem()
	{
		// The two good edits precede the bad one, so a naive implementation that mutated as it
		// went would leave the body two-thirds patched. It must be byte-identical instead.
		var v = await SeedAsync();

		var r = await UpsertAsync(new NodePatch
		{
			Key = "n",
			Version = v,
			Fragment = [E("first paragraph", "1st"), E("second paragraph", "2nd"), E("NOT PRESENT", "x")],
		});

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("fragment[2]");

		var n = await NodeAsync();
		n.Body.Should().Be(Original);            // not "1st", not "2nd" — nothing
		n.Version.Should().Be(v);
	}

	// ── body + fragment ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task BodyAndFragmentTogether_IsRefused_AndNeitherIsApplied()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(new NodePatch
		{
			Key = "n",
			Version = v,
			Body = "a whole new body",
			Fragment = [E("first paragraph", "1st")],   // this one WOULD have matched uniquely
		});

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("mutually exclusive");

		var n = await NodeAsync();
		n.Body.Should().Be(Original);            // neither the full body nor the fragment landed
	}

	// ── a fragment needs something to patch ──────────────────────────────────────────

	[Fact]
	public async Task FragmentOnANodeThatDoesNotExist_IsRefused_AndNoNodeIsCreated()
	{
		// Resolving against an absent row as if it were "" would report success for a create the
		// caller never asked for.
		var r = await UpsertAsync(new NodePatch { Key = "ghost", Title = "G", Version = 0, Fragment = [E("x", "y")] });

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("no active revision");
		(await _tasks.GetAsync(Proj, Board)).Nodes.Should().NotContain(n => n.Key == "ghost");
	}

	// ── the version watermark still rules ────────────────────────────────────────────

	[Fact]
	public async Task StaleBaseline_IsStillStale_EvenWhenTheFragmentWouldHaveMatched()
	{
		// The fragment is resolved in the read-merge, but it does NOT bypass the watermark: a
		// caller whose baseline predates a concurrent edit is refused as Stale, not silently
		// rebased onto text it never read.
		var v = await SeedAsync();
		await UpsertAsync(new NodePatch { Key = "n", Version = v, Title = "moved by someone else" });
		var moved = await NodeAsync();
		moved.Version.Should().BeGreaterThan(v);

		var r = await UpsertAsync(new NodePatch { Key = "n", Version = v, Fragment = [E("first paragraph", "1st")] });

		r.Result.Applied.Should().BeFalse();
		r.Result.Conflicts.Should().ContainSingle().Which.Kind.Should().Be(TemporalConflictKind.Stale);
		(await NodeAsync()).Body.Should().Be(Original);
	}

	// ── batch behaviour ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task Atomic_ABadFragmentAbortsTheWholeBatch_TheCleanSiblingDoesNotLand()
	{
		var v = await SeedAsync();

		var r = await UpsertAsync(
			new NodePatch { Key = "n", Version = v, Fragment = [E("NOT PRESENT", "x")] },
			new NodePatch { Key = "sibling", Title = "S", Body = "s", Version = 0 });

		r.Result.Applied.Should().BeFalse();
		(await _tasks.GetAsync(Proj, Board)).Nodes.Should().NotContain(n => n.Key == "sibling");
		(await NodeAsync()).Body.Should().Be(Original);
	}

	[Fact]
	public async Task Partial_ABadFragmentIsRejectedPerNode_TheCleanSiblingLands()
	{
		var v = await SeedAsync();

		var r = await _tasks.UpsertAsync(Proj, Board,
			[
				new NodePatch { Key = "n", Version = v, Fragment = [E("NOT PRESENT", "x")] },
				new NodePatch { Key = "sibling", Title = "S", Body = "s", Version = 0 },
			],
			actor: null, atomic: false);

		r.Result.Applied.Should().BeTrue();                                    // the batch DID write
		r.Result.Added.Select(n => n.Key).Should().Equal("sibling");
		r.Result.Conflicts.Should().ContainSingle().Which.Key.Should().Be("n");
		(await NodeAsync()).Body.Should().Be(Original);                        // but not the fragment node
	}
}
