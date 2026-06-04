using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Web;

// The per-node detail page (TaskBoardNode: /ui/{ws}/{project}/tasks/node/{nodeId}) and the
// cross-board GetNodeAsync that powers it: a node is addressed by its stable NodeId alone, so
// resolution must not need the board, must rebuild the part_of breadcrumb, and surface the thread.
[Collection("DataModule")]
public sealed class TaskBoardNodePageTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly CommentService _comments;
	readonly TasksService _tasks;

	public TaskBoardNodePageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-plannode-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_comments = new CommentService(_factory);
		_tasks = new TasksService(_store, new RelationStore(_db), new TagStore(_factory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static FeatureFlags Flags(bool tasks = true) =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = tasks ? "true" : "false" }).Build());

	async Task Upsert(string board, params NodePatch[] nodes) =>
		await _tasks.UpsertAsync(Proj, board, nodes);

	string NodeId(string board, string key) =>
		_store.GetContext(Proj).PlanNodes.Where(n => n.Board == board && n.Key == key && n.ActiveTo == null).ToList().Single().NodeId;

	TaskBoardNodeModel Page(bool tasks = true) =>
		new(Flags(tasks), _tasks, _comments) { WorkspaceKey = "ws", ProjectKey = Proj };

	[Fact]
	public async Task GetNodeAsync_ResolvesByIdAcrossBoards_WithoutKnowingTheBoard()
	{
		await Upsert("alpha", new NodePatch { Key = "a", Title = "Node A", Body = "body-a" });
		await Upsert("beta", new NodePatch { Key = "b", Title = "Node B", Body = "body-b" });

		var a = await _tasks.GetNodeAsync(Proj, NodeId("alpha", "a"));
		var b = await _tasks.GetNodeAsync(Proj, NodeId("beta", "b"));

		a!.Board.Should().Be("alpha");
		a.Node.Key.Should().Be("a");
		a.Node.Body.Should().Be("body-a");
		b!.Board.Should().Be("beta");
		b.Node.Key.Should().Be("b");
	}

	[Fact]
	public async Task GetNodeAsync_BuildsPartOfAncestorChain_RootToParent()
	{
		await Upsert("plan",
			new NodePatch { Key = "root", Title = "Root" },
			new NodePatch { Key = "mid", PartOf = "root", Title = "Mid" },
			new NodePatch { Key = "leaf", PartOf = "mid", Title = "Leaf", Body = "the full leaf body" });

		var detail = await _tasks.GetNodeAsync(Proj, NodeId("plan", "leaf"));

		detail!.Node.Key.Should().Be("leaf");
		detail.Ancestors.Select(x => x.Slug).Should().Equal("root", "mid"); // root → parent order
	}

	[Fact]
	public async Task GetNodeAsync_UnknownId_ReturnsNull()
	{
		(await _tasks.GetNodeAsync(Proj, "no-such-node-id")).Should().BeNull();
	}

	[Fact]
	public async Task OnGet_RendersNode_WithFullBodyAndThread()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "full body text" });
		var id = NodeId("plan", "n");
		await _comments.AddAsync(Proj, "plan", id, parentId: null, author: "t", body: "a remark", tags: null);

		var page = Page();
		page.NodeId = id;
		var result = await page.OnGetAsync(default);

		result.Should().BeOfType<PageResult>();
		page.Detail.Node.Body.Should().Be("full body text");
		page.Detail.Board.Should().Be("plan");
		page.Thread.Should().ContainSingle().Which.Comment.Body.Should().Be("a remark");
	}

	[Fact]
	public async Task OnGet_UnknownId_ReturnsNotFound()
	{
		var page = Page();
		page.NodeId = "no-such-node-id";
		(await page.OnGetAsync(default)).Should().BeOfType<NotFoundResult>();
	}

	[Fact]
	public async Task OnGet_FeatureDisabled_ReturnsNotFound()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var page = Page(tasks: false);
		page.NodeId = NodeId("plan", "n");
		(await page.OnGetAsync(default)).Should().BeOfType<NotFoundResult>();
	}
}
