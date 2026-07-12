using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// commits[] replaces the single CommitRef (node-commits-impl): a node's commits are an SCD-2
// set in plan_node_commits, attached to its stable NodeId — many per node, PATCH semantics
// (null = don't touch, [] = clear), normalized (trim/lowercase/dedupe) and validated (hex,
// 7..40), and reverse-lookable by exact sha or a >=7-hex prefix.
public sealed class NodeCommitsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public NodeCommitsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-commits-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
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

	static NodePatch Node(string key, string? title = null, IReadOnlyList<string>? commits = null, string? status = null, long version = 0) => new()
	{
		Key = key,
		Title = title ?? key,
		Body = "body of " + key,
		Status = status,
		Commits = commits,
		Version = version,
	};

	async Task<(string NodeId, long Version)> NodeInfo(string board, string key)
	{
		var view = await _tasks.GetAsync(Proj, board);
		var n = view.Nodes.Single(n => n.Key == key);
		return (n.NodeId, n.Version);
	}

	Task<IReadOnlyList<string>> ViewCommits(string board, string key) =>
		_tasks.GetAsync(Proj, board).ContinueWith(t => t.Result.Nodes.Single(n => n.Key == key).Commits);

	List<PlanNodeCommit> Rows(string nodeId) =>
		_store.GetContext(Proj).PlanNodeCommits.Where(c => c.NodeId == nodeId).ToList();

	[Fact]
	public async Task Upsert_WithCommits_Persists_AndReadSurfacesReturnTheArray()
	{
		var up = await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", commits: ["abc1234", "deadbeef"]) });

		(await ViewCommits("b", "t")).Should().BeEquivalentTo("abc1234", "deadbeef");
		// The write-ack echo carries the array too.
		up.Result.Added.Single(n => n.Key == "t").Commits.Should().BeEquivalentTo("abc1234", "deadbeef");
	}

	[Fact]
	public async Task SecondUpsert_DifferentSet_ClosesRemoved_AddsNew_TemporalDiff()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", commits: ["aaaaaaa", "bbbbbbb"]) });
		var (id, ver) = await NodeInfo("b", "t");

		// keep bbbbbbb, drop aaaaaaa, add ccccccc.
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", commits: ["bbbbbbb", "ccccccc"], version: ver) });

		(await ViewCommits("b", "t")).Should().BeEquivalentTo("bbbbbbb", "ccccccc");
		var rows = Rows(id);
		rows.Single(r => r.Sha == "aaaaaaa").ValidTo.Should().NotBeNull(); // closed
		rows.Single(r => r.Sha == "bbbbbbb").ValidTo.Should().BeNull();    // untouched, still one active row
		rows.Where(r => r.Sha == "bbbbbbb").Should().ContainSingle();      // not re-inserted
		rows.Single(r => r.Sha == "ccccccc").ValidTo.Should().BeNull();    // added
	}

	[Fact]
	public async Task NullCommits_LeavesSetUntouched_EmptyClears()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", commits: ["abc1234"]) });
		var (_, ver) = await NodeInfo("b", "t");

		// A patch that omits commits (null) touches a different field; the set is unchanged.
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", title: "renamed", version: ver) });
		(await ViewCommits("b", "t")).Should().BeEquivalentTo("abc1234");

		var (_, ver2) = await NodeInfo("b", "t");
		// [] explicitly clears.
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", commits: [], version: ver2) });
		(await ViewCommits("b", "t")).Should().BeEmpty();
	}

	[Fact]
	public async Task Normalization_TrimsLowercasesDedupes_AndRejectsNonHex()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("t", commits: ["  ABC1234 ", "abc1234", "DeadBeef"]) });
		(await ViewCommits("b", "t")).Should().BeEquivalentTo("abc1234", "deadbeef");

		var badHex = async () => await _tasks.UpsertAsync(Proj, "b", new[] { Node("z", commits: ["nothex!"]) });
		await badHex.Should().ThrowAsync<ArgumentException>().WithMessage("*commit*");

		var tooShort = async () => await _tasks.UpsertAsync(Proj, "b", new[] { Node("z", commits: ["abc12"]) });
		await tooShort.Should().ThrowAsync<ArgumentException>().WithMessage("*7..40*");
	}

	[Fact]
	public async Task ReverseLookup_Exact_And_SevenHexPrefix()
	{
		const string full = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("carrier", commits: [full]) });
		await _tasks.UpsertAsync(Proj, "b", new[] { Node("other", commits: ["fedcba9"]) });

		async Task<IReadOnlyList<string>> Find(string commit) =>
			(await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
			{
				Filter = new TaskNodeFilter(Commit: commit),
			})).Hits.Select(h => h.Node.Key).ToList();

		(await Find(full)).Should().BeEquivalentTo("carrier");          // exact full sha
		(await Find("a1b2c3d")).Should().BeEquivalentTo("carrier");     // 7-hex prefix resolves the full sha
		(await Find("fedcba9")).Should().BeEquivalentTo("other");       // exact short sha
		(await Find("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0")).Should().BeEquivalentTo("carrier");
		(await Find("deadbee")).Should().BeEmpty();                     // no carrier
	}

	// M011 seeds plan_node_commits from the old CommitRef column, then drops it (table rebuild).
	// A legacy M001-era file with a non-null CommitRef on its active row must migrate the commit
	// into the new table on first open, and the CommitRef column must be gone from plan_nodes.
	[Fact]
	public void Migration_SeedsCommitsFromLegacyCommitRef_AndDropsColumn()
	{
		var dir = Path.Combine(Path.GetTempPath(), "petbox-commitmig-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var cs = $"Data Source={Path.Combine(dir, "roadmap.db")}";
		try
		{
			using (var c = new SqliteConnection(cs))
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
						('with','1',2,'W','x','ABCDEF1',0,1,NULL,'2026-01-01','2026-01-01'),
						('without','1',2,'N','x',NULL,0,1,NULL,'2026-01-01','2026-01-01');
					""";
				cmd.ExecuteNonQuery();
			}
			TestDirs.ClearPoolsUnder(dir);

			TasksSchema.Ensure(cs); // runs M002..M011 in place

			using var db = new TasksDb(TasksDb.CreateOptions(cs));
			var withId = db.PlanNodes.Single(n => n.Key == "with" && n.ActiveTo == null).NodeId;
			var withoutId = db.PlanNodes.Single(n => n.Key == "without" && n.ActiveTo == null).NodeId;

			db.PlanNodeCommits.Where(x => x.NodeId == withId && x.ValidTo == null)
				.Select(x => x.Sha).ToList().Should().BeEquivalentTo("abcdef1"); // lowercased
			db.PlanNodeCommits.Any(x => x.NodeId == withoutId).Should().BeFalse();

			// The CommitRef column is gone (table rebuild) — selecting it fails.
			var probe = () => db.Execute<long>("SELECT count(CommitRef) FROM plan_nodes");
			probe.Should().Throw<Exception>();
		}
		finally
		{
			TestDirs.CleanupOrDefer(dir);
		}
	}
}
