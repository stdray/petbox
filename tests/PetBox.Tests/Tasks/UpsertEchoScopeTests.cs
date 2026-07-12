using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// Echo-covers-the-call (spec sinceversion-contract): a tasks_upsert is a pure write-ack —
// it echoes ONLY the nodes of THIS call (its patches + its own cascade closures), never
// other writers' history — an insert of 3 nodes on a live board must not return 78 foreign
// nodes. The write carries NO cursor parameter; the full board delta since a cursor lives
// exclusively on tasks_delta, and `currentVersion` stays the board-wide cursor for it.
public sealed class UpsertEchoScopeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public UpsertEchoScopeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-echoscope-" + Guid.NewGuid().ToString("N"));
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

	static NodePatch Node(string key, string? title = null, string? status = null,
		string? blockedBy = null, string? supersedes = null, long version = 0) => new()
		{
			Key = key,
			Title = title ?? key.ToUpperInvariant(),
			Body = "body of " + key,
			Status = status,
			BlockedBy = blockedBy,
			Supersedes = supersedes,
			Version = version,
		};

	// Seed a board with `count` foreign nodes (another writer's history).
	async Task<long> SeedForeignAsync(string board, int count)
	{
		var r = await _tasks.UpsertAsync(Proj, board,
			Enumerable.Range(0, count).Select(i => Node($"foreign-{i}")).ToArray());
		r.Result.Applied.Should().BeTrue();
		return r.Result.CurrentVersion;
	}

	[Fact]
	public async Task Echo_ContainsOnlyThisCallsNodes_DespiteForeignHistory()
	{
		await SeedForeignAsync("b", 10);

		// The write used to re-dump the whole board on a stale cursor; the ack is scoped
		// to the call, always.
		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Node("mine-1"), Node("mine-2") });

		r.Result.Applied.Should().BeTrue();
		r.Result.Added.Select(n => n.Key).Should().BeEquivalentTo("mine-1", "mine-2");
		r.Result.Updated.Should().BeEmpty();
		r.Result.Removed.Should().BeEmpty();
		// The cursor stays board-wide: a follow-up delta from it is empty.
		(await _tasks.DeltaAsync(Proj, "b", r.Result.CurrentVersion)).Result.Added.Should().BeEmpty();
	}

	// The write-ack no longer carries a full-delta mode (includeDelta is gone): the delta
	// since a cursor is tasks_delta's job, and it returns the exact equivalent.
	[Fact]
	public async Task FullBoardDelta_LivesOnTasksDelta_NotOnTheWriteAck()
	{
		await SeedForeignAsync("b", 5);

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Node("mine") });
		r.Result.Added.Concat(r.Result.Updated).Select(n => n.Key).Should().Equal("mine"); // pure ack

		var delta = await _tasks.DeltaAsync(Proj, "b", 0);
		var echoed = delta.Result.Added.Concat(delta.Result.Updated).Select(n => n.Key).ToList();
		echoed.Should().Contain("mine");
		echoed.Should().Contain(Enumerable.Range(0, 5).Select(i => $"foreign-{i}")); // full delta
	}

	[Fact]
	public async Task Conflicts_ReportedOnTheAck()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("n") });                              // v1
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("n", title: "N-mid", version: 1) });  // -> v2 (concurrent edit)

		// A genuine stale baseline: the node moved to v2 while this author still holds v1.
		// (Was version:999 under exact-match — that is now a FutureBaseline, not Stale.)
		var stale = await _tasks.UpsertAsync(Proj, "b", new[] { Node("n", title: "N2", version: 1) });
		stale.Result.Applied.Should().BeFalse();
		stale.Result.Conflicts.Should().ContainSingle().Which.Kind.Should().Be(TemporalConflictKind.Stale);
	}

	[Fact]
	public async Task SupersedesCascade_ObsoletedNodeVisibleInDefaultEcho()
	{
		await SeedForeignAsync("b", 3);
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("old-way") });

		// `supersedes` moves the target to its terminal-cancel — that cascade closure is part
		// of what THIS call did, so it must show in the scoped echo (unlike the foreign nodes).
		var r = await _tasks.UpsertAsync(Proj, "b", new[] { Node("new-way", supersedes: "old-way") });

		r.Result.Added.Select(n => n.Key).Should().BeEquivalentTo("new-way");
		var obsoleted = r.Result.Updated.Should().ContainSingle().Subject;
		obsoleted.Key.Should().Be("old-way");
		obsoleted.Status.Should().Be("Cancelled");
		r.Result.Added.Concat(r.Result.Updated).Select(n => n.Key).Should().NotContain(k => k.StartsWith("foreign-"));
	}

	[Fact]
	public async Task DeleteCascade_SubtreeRemoval_AndUnblock_VisibleInDefaultEcho()
	{
		await SeedForeignAsync("b", 3);

		// A blocker chain: deleting the blocker unblocks the stuck node (Blocked → InProgress).
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("blocker") });
		var blockerId = (await _tasks.GetAsync(Proj, "b")).Nodes.Single(n => n.Key == "blocker").NodeId;
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("stuck", blockedBy: blockerId, status: "Blocked") });

		var r = await _tasks.UpsertAsync(Proj, "b", new[] { new NodePatch { Key = "blocker", Deleted = true } });

		r.Result.Applied.Should().BeTrue();
		r.Result.Removed.Should().Equal("blocker"); // the deleted key, no foreign removals
		var unblocked = r.Result.Updated.Should().ContainSingle().Subject; // the unblock cascade
		unblocked.Key.Should().Be("stuck");
		unblocked.Status.Should().Be("InProgress");
	}
}
