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
		TestSchema.Core(cs);
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

	[Fact]
	public async Task OnGet_ExposesLegalNextStatuses()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await Upsert("ideas", new NodePatch { Key = "i", Type = "idea", Title = "I" }); // born raw

		var page = Page();
		page.NodeId = NodeId("ideas", "i");
		await page.OnGetAsync(default);

		page.NextStatuses.Should().Equal("exploring"); // raw -> exploring is the only edge
	}

	[Fact]
	public async Task OnPostEdit_UpdatesTitleAndBody_ThroughTheService()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "old", Body = "old body" });
		var id = NodeId("plan", "n");
		var version = (await _tasks.GetNodeAsync(Proj, id))!.Node.Version;

		var page = Page();
		page.NodeId = id;
		var result = await page.OnPostEditAsync("new title", "new **body**", version, default);

		// PRG to the CANONICAL slug-URL (node-slug-addressable), not the opaque alias.
		result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/ui/ws/{Proj}/tasks/plan/n");
		var after = (await _tasks.GetNodeAsync(Proj, id))!.Node;
		after.Title.Should().Be("new title");
		after.Body.Should().Be("new **body**");
	}

	[Fact]
	public async Task OnPostStatus_AppliesLegalTransition()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await Upsert("ideas", new NodePatch { Key = "i", Type = "idea", Title = "I" }); // raw
		var id = NodeId("ideas", "i");
		var version = (await _tasks.GetNodeAsync(Proj, id))!.Node.Version;

		var page = Page();
		page.NodeId = id;
		var result = await page.OnPostStatusAsync("exploring", version, default);

		result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/ui/ws/{Proj}/tasks/ideas/i");
		(await _tasks.GetNodeAsync(Proj, id))!.Node.Status.Should().Be("exploring");
	}

	[Fact]
	public async Task OnPostStatus_IllegalTransition_RerendersWithError()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await Upsert("ideas", new NodePatch { Key = "i", Type = "idea", Title = "I" }); // raw
		var id = NodeId("ideas", "i");
		var version = (await _tasks.GetNodeAsync(Proj, id))!.Node.Version;

		var page = Page();
		page.NodeId = id;
		var result = await page.OnPostStatusAsync("accepted", version, default); // raw -> accepted: no edge

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		(await _tasks.GetNodeAsync(Proj, id))!.Node.Status.Should().Be("raw"); // unchanged
	}

	[Fact]
	public async Task OnPostEdit_StaleVersion_RerendersWithConflict()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "t", Body = "b" });
		var id = NodeId("plan", "n");
		var version = (await _tasks.GetNodeAsync(Proj, id))!.Node.Version;

		var page = Page();
		page.NodeId = id;
		var result = await page.OnPostEditAsync("x", "y", version + 5, default); // baseline the user never saw

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		(await _tasks.GetNodeAsync(Proj, id))!.Node.Title.Should().Be("t"); // unchanged
	}

	// edit-respects-guards: a spec-node edit from the UI goes through UpsertAsync, which demands an
	// ideaRef (accepted idea) on every spec change. The edit handler sends none, so the guard
	// rejects it — the UI can't bypass the rule. (The accepted idea is created directly here:
	// the approve gate is convention, not enforced in the engine.)
	[Fact]
	public async Task OnPostEdit_SpecNodeWithoutIdeaRef_RejectedByGuard()
	{
		await _tasks.CreateBoardAsync(Proj, "ideas", "ideas", null, null);
		await _tasks.CreateBoardAsync(Proj, "spec", "spec", null, null);
		await Upsert("ideas", new NodePatch { Key = "i", Type = "idea", Status = "accepted", Title = "I" });
		var ideaId = NodeId("ideas", "i");
		await Upsert("spec", new NodePatch { Key = "s", Type = "spec", Status = "defined", Title = "S", Body = "req", IdeaRef = ideaId });
		var id = NodeId("spec", "s");
		var version = (await _tasks.GetNodeAsync(Proj, id))!.Node.Version;

		var page = Page();
		page.NodeId = id;
		var result = await page.OnPostEditAsync("S edited", "req2", version, default);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().Contain("ideaRef");
		(await _tasks.GetNodeAsync(Proj, id))!.Node.Title.Should().Be("S"); // unchanged
	}

	// node-slug-addressable: the canonical (board, slug) address resolves to the same enriched
	// node view as the opaque NodeId, so the human-readable URL renders the detail page.
	[Fact]
	public async Task GetNodeBySlugAsync_ResolvesByBoardAndSlug()
	{
		await Upsert("alpha", new NodePatch { Key = "a", Title = "Node A", Body = "body-a" });
		await Upsert("beta", new NodePatch { Key = "a", Title = "Other A", Body = "body-beta" }); // same slug, other board

		var a = await _tasks.GetNodeBySlugAsync(Proj, "alpha", "a");

		a!.Board.Should().Be("alpha");
		a.Node.Key.Should().Be("a");
		a.Node.Body.Should().Be("body-a"); // board segment disambiguates the cross-board slug
	}

	[Fact]
	public async Task GetNodeBySlugAsync_UnknownSlug_ReturnsNull()
	{
		await Upsert("alpha", new NodePatch { Key = "a", Title = "A" });
		(await _tasks.GetNodeBySlugAsync(Proj, "alpha", "nope")).Should().BeNull();
		(await _tasks.GetNodeBySlugAsync(Proj, "nope", "a")).Should().BeNull();
	}

	// The thread must load on the slug route too: comments are fetched by the RESOLVED node id,
	// not the bound NodeId (empty here), else spec_plan/discussion vanish on the canonical URL.
	[Fact]
	public async Task OnGet_ResolvesByBoardSlug_RendersNodeWithThread()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "full body text" });
		await _comments.AddAsync(Proj, "plan", NodeId("plan", "n"), parentId: null, author: "t", body: "a remark", tags: null);

		var page = Page();
		page.Board = "plan";
		page.Slug = "n"; // slug route, no NodeId
		var result = await page.OnGetAsync(default);

		result.Should().BeOfType<PageResult>();
		page.Detail.Node.Key.Should().Be("n");
		page.Detail.Node.Body.Should().Be("full body text");
		page.Thread.Should().ContainSingle().Which.Comment.Body.Should().Be("a remark");
	}

	[Fact]
	public async Task OnGet_UnknownSlug_ReturnsNotFound()
	{
		var page = Page();
		page.Board = "plan";
		page.Slug = "missing";
		(await page.OnGetAsync(default)).Should().BeOfType<NotFoundResult>();
	}

	// A node filed via ReportIssueAsync writes straight to TemporalStore (skipping ApplyWorkflow,
	// the usual NodeId-assignment point). It must still get a stable NodeId, else its canonical
	// slug-URL 404s: FindNodeIdBySlug returns "" and GetNode short-circuits on the empty id.
	[Fact]
	public async Task ReportIssue_AssignsNodeId_AndResolvesBySlug()
	{
		var key = await _tasks.ReportIssueAsync(Proj, "client-issues", "Something is broken", "details");

		NodeId("client-issues", key).Should().NotBeNullOrEmpty();
		var detail = await _tasks.GetNodeBySlugAsync(Proj, "client-issues", key);
		detail!.Node.Key.Should().Be(key);
		detail.Node.Title.Should().Be("Something is broken");
	}

	// `node` is reserved as a board name so /tasks/node/{nodeId} can't collide with the slug
	// route /tasks/{board}/{slug} (node-slug-addressable).
	[Fact]
	public async Task CreateBoard_NameNode_Rejected()
	{
		var act = () => _tasks.CreateBoardAsync(Proj, "node", null, null, null);
		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*reserved*");
	}
}
