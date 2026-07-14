using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// board-search-stem-lookup: TasksService.GetBoardChangeStampAsync is the cache/ETag probe
// TaskBoard.cshtml.cs's OnGetSearchIndexAsync uses to decide "has this board changed" without
// materializing every node. Its whole reason to compose TWO sources (plan_nodes.Version AND a
// node_tag mutation timestamp), rather than just Version, is this: a tag is NOT part of
// PlanNode.SamePayload, so TagStore.SetAsync (called from a tags-only edit) writes node_tag
// directly and never mints a new plan_nodes revision — the node's own Version is untouched. A
// probe over Version alone would then serve a stale cached search-index lookup (304, old body)
// after a pure tag edit. This is exactly the review finding an earlier draft of the probe missed
// (it queried plan_nodes alone) — these tests pin the fix so it can't regress silently.
public sealed class BoardChangeStampTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "b";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TagStore _tags;
	readonly TasksService _tasks;

	public BoardChangeStampTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-changestamp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_tags = new TagStore(_factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), _tags, new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	async Task<string> SeedUntaggedNode()
	{
		await _store.CreateAsync(Proj, Board, null, "simple");
		var ack = await _tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = "n1", Title = "N1", Body = "x" }]);
		return ack.Result.Added.Single().NodeId;
	}

	[Fact]
	public async Task TagOnlyChange_ChangesTheStamp_EvenWithNoNodeEdit()
	{
		var nodeId = await SeedUntaggedNode();
		var before = await _tasks.GetBoardChangeStampAsync(Proj, Board);

		// Tags go through ITagStore directly (NOT tasks.UpsertAsync) — isolates the exact
		// scenario the probe exists for: node_tag changes, plan_nodes does not.
		await _tags.SetAsync(Proj, Board, nodeId, ["area:ui"]);
		var afterAdd = await _tasks.GetBoardChangeStampAsync(Proj, Board);

		afterAdd.NodeVersion.Should().Be(before.NodeVersion,
			"a tags-only edit must NOT bump the node's own Version — tags aren't part of PlanNode.SamePayload");
		afterAdd.TagStamp.Should().NotBe(before.TagStamp);
		(afterAdd.TagStamp > before.TagStamp || before.TagStamp is null).Should().BeTrue("the stamp must move forward, never backward or stay put");
	}

	[Fact]
	public async Task TagRemoval_AlsoChangesTheStamp_NotJustAddition()
	{
		// The whole reason the probe uses coalesce(ValidTo, ValidFrom) instead of max(ValidFrom)
		// alone: a REMOVAL stamps ValidTo on the existing row without moving its ValidFrom, so a
		// probe that only ever looked at ValidFrom would not react to a removal at all.
		var nodeId = await SeedUntaggedNode();
		await _tags.SetAsync(Proj, Board, nodeId, ["area:ui"]);
		var afterAdd = await _tasks.GetBoardChangeStampAsync(Proj, Board);

		await _tags.SetAsync(Proj, Board, nodeId, []); // remove every tag
		var afterRemove = await _tasks.GetBoardChangeStampAsync(Proj, Board);

		afterRemove.NodeVersion.Should().Be(afterAdd.NodeVersion, "still no node payload edit");
		afterRemove.TagStamp.Should().NotBeNull();
		afterRemove.TagStamp.Should().BeAfter(afterAdd.TagStamp!.Value, "the removal must move the stamp forward again, past the earlier addition");
	}

	[Fact]
	public async Task NoTagsEver_TagStampIsNull()
	{
		await SeedUntaggedNode();
		var stamp = await _tasks.GetBoardChangeStampAsync(Proj, Board);
		stamp.TagStamp.Should().BeNull();
		stamp.NodeVersion.Should().BeGreaterThan(0);
	}

	[Fact]
	public async Task PlainNodeEdit_BumpsNodeVersion_TagStampUnaffectedByThatEditAlone()
	{
		var nodeId = await SeedUntaggedNode();
		await _tags.SetAsync(Proj, Board, nodeId, ["area:ui"]);
		var afterTag = await _tasks.GetBoardChangeStampAsync(Proj, Board);

		// A title edit through the normal upsert path — payload changes, tags untouched.
		await _tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = "n1", Title = "N1 renamed", Body = "x", Version = afterTag.NodeVersion }]);
		var afterEdit = await _tasks.GetBoardChangeStampAsync(Proj, Board);

		afterEdit.NodeVersion.Should().BeGreaterThan(afterTag.NodeVersion);
		afterEdit.TagStamp.Should().Be(afterTag.TagStamp, "a node-payload-only edit must not touch the tag stamp");
	}
}
