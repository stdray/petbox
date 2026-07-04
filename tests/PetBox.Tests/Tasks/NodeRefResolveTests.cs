using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// Batch `[[slug]]` mention resolution (node-ref-autolink-impl): ITasksService.ResolveSlugsAsync
// maps each requested slug to its live node's current location, matching BOTH current keys AND
// former keys (PrevKey rename lineage), dropping ambiguous slugs (same slug on 2+ boards) and
// misses. The renderer turns the resulting map into links; this covers the resolution logic.
public sealed class NodeRefResolveTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public NodeRefResolveTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-noderef-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_tasks = new TasksService(_store, new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static NodePatch Node(string key, string? title = null, string? prevKey = null, long version = 0) => new()
	{
		Key = key,
		Title = title ?? key,
		Body = "body of " + key,
		Status = "Todo",
		PrevKey = prevKey,
		Version = version,
	};

	async Task<long> Version(string board, string key)
	{
		var view = await _tasks.GetAsync(Proj, board, includeClosed: true);
		return view.Nodes.Single(n => n.Key == key).Version;
	}

	[Fact]
	public async Task CurrentSlug_Resolves_ToBoardKeyTitleNodeId()
	{
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("auth-flow", title: "Auth Flow") });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "auth-flow" });

		map.Should().ContainKey("auth-flow");
		var r = map["auth-flow"];
		r.Board.Should().Be("spec");
		r.Key.Should().Be("auth-flow");
		r.Title.Should().Be("Auth Flow");
		r.NodeId.Should().NotBeNullOrEmpty();
	}

	[Fact]
	public async Task Miss_IsOmitted()
	{
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("real-node") });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "no-such-node" });

		map.Should().NotContainKey("no-such-node");
		map.Should().BeEmpty();
	}

	[Fact]
	public async Task FormerSlug_Resolves_ToCurrentLocation_AfterRename()
	{
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("old-name", title: "The Node") });
		var v = await Version("spec", "old-name");
		// Rename old-name -> new-name (same NodeId, PrevKey lineage).
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("new-name", title: "The Node", prevKey: "old-name", version: v) });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "old-name", "new-name" });

		// The FORMER slug still resolves — to the node's CURRENT key.
		map.Should().ContainKey("old-name");
		map["old-name"].Key.Should().Be("new-name");
		map["old-name"].Board.Should().Be("spec");
		// The current slug resolves too, to the same node.
		map["new-name"].Key.Should().Be("new-name");
		map["old-name"].NodeId.Should().Be(map["new-name"].NodeId);
	}

	[Fact]
	public async Task FormerSlug_MultiStepRename_ResolvesThroughChain()
	{
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("a") });
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("b", prevKey: "a", version: await Version("spec", "a")) });
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("c", prevKey: "b", version: await Version("spec", "b")) });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "a", "b", "c" });

		map["a"].Key.Should().Be("c");
		map["b"].Key.Should().Be("c");
		map["c"].Key.Should().Be("c");
	}

	[Fact]
	public async Task AmbiguousSlug_OnTwoBoards_IsExcluded()
	{
		await _tasks.UpsertAsync(Proj, "board-a", new[] { Node("dup") });
		await _tasks.UpsertAsync(Proj, "board-b", new[] { Node("dup") });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "dup" });

		// Same slug on 2+ boards → ambiguous → omitted (renders literal).
		map.Should().NotContainKey("dup");
	}

	[Fact]
	public async Task CurrentKey_WinsOverAnotherNodesFormerKey()
	{
		// Node X renamed reused -> renamed; then a NEW node is created reusing the freed slug.
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("reused") });
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("renamed", prevKey: "reused", version: await Version("spec", "reused")) });
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("reused", title: "Fresh Node") });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "reused" });

		// The live current node named `reused` wins over the former-key of `renamed`.
		map.Should().ContainKey("reused");
		map["reused"].Key.Should().Be("reused");
		map["reused"].Title.Should().Be("Fresh Node");
	}

	[Fact]
	public async Task Batch_MixedSlugs_ResolvesEachIndependently()
	{
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("keep") });
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("was-here") });
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("now-here", prevKey: "was-here", version: await Version("spec", "was-here")) });
		await _tasks.UpsertAsync(Proj, "x", new[] { Node("clash") });
		await _tasks.UpsertAsync(Proj, "y", new[] { Node("clash") });

		var map = await _tasks.ResolveSlugsAsync(Proj, new[] { "keep", "was-here", "clash", "ghost" });

		map.Should().ContainKey("keep");          // current
		map["was-here"].Key.Should().Be("now-here"); // former → current
		map.Should().NotContainKey("clash");      // ambiguous
		map.Should().NotContainKey("ghost");      // miss
	}

	[Fact]
	public async Task EmptyInput_ReturnsEmpty()
	{
		await _tasks.UpsertAsync(Proj, "spec", new[] { Node("a") });
		(await _tasks.ResolveSlugsAsync(Proj, Array.Empty<string>())).Should().BeEmpty();
	}
}
