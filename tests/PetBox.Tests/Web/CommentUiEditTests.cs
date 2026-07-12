using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

// Work card comments-ui-edit: the POST handlers (add/reply/edit/delete) that turn the read-only
// comments v1 thread into an editable one, on both surfaces that render _CommentThread — the
// per-node detail page (TaskBoardNodeModel) and the board page (TaskBoardModel, many node cards
// per page). Every handler routes through ICommentService, the SAME door the comments_* MCP
// tools use — these tests assert the wiring (redirect/PRG, error surfacing, guard rejection),
// not comment-service business rules already covered elsewhere.
public sealed class CommentUiEditTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly CommentService _comments;
	readonly TasksService _tasks;

	public CommentUiEditTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-commentui-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_comments = new CommentService(_factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), new TagStore(_factory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Flags(bool tasks = true) =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = tasks ? "true" : "false" }).Build());

	async Task Upsert(string board, params NodePatch[] nodes) =>
		await _tasks.UpsertAsync(Proj, board, nodes);

	string NodeId(string board, string key) =>
		_store.GetContext(Proj).PlanNodes.Where(n => n.Board == board && n.Key == key && n.ActiveTo == null).ToList().Single().NodeId;

	// Give the bare PageModel an HttpContext so `User.Identity?.Name` (the comment author) reads
	// without an NRE outside a real request pipeline — same need as MutationFeedbackPageTests.
	static void Wire(PageModel page) =>
		page.PageContext = new PageContext { HttpContext = new DefaultHttpContext() };

	TaskBoardNodeModel NodePage(bool tasks = true)
	{
		var page = new TaskBoardNodeModel(Flags(tasks), _tasks, _comments, new NullSettingsResolver()) { WorkspaceKey = "ws", ProjectKey = Proj };
		Wire(page);
		return page;
	}

	TaskBoardModel BoardPage(string board, bool tasks = true)
	{
		var page = new TaskBoardModel(Flags(tasks), _tasks, _comments, new NullSettingsResolver()) { WorkspaceKey = "ws", ProjectKey = Proj, Board = board };
		Wire(page);
		return page;
	}

	// ── TaskBoardNodeModel (per-node detail page) ───────────────────────────────

	[Fact]
	public async Task Node_OnPostCommentAdd_AddsRootComment_AndRedirectsToCanonicalUrl()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentAddAsync(parentId: null, body: "first remark", default);

		result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/ui/ws/{Proj}/tasks/plan/n");
		var thread = await _comments.ListForNodeAsync(Proj, "plan", id);
		thread.Should().ContainSingle().Which.Body.Should().Be("first remark");
	}

	[Fact]
	public async Task Node_OnPostCommentAdd_WithParentId_AddsReply()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var root = await _comments.AddAsync(Proj, "plan", id, null, "t", "root remark", null);

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentAddAsync(root.Id, "a reply", default);

		result.Should().BeOfType<RedirectResult>();
		var thread = await _comments.ListForNodeAsync(Proj, "plan", id);
		thread.Should().HaveCount(2);
		thread.Single(c => c.Body == "a reply").ParentId.Should().Be(root.Id);
	}

	[Fact]
	public async Task Node_OnPostCommentAdd_InvalidParent_RerendersWithError()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentAddAsync("no-such-comment", "orphan reply", default);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		(await _comments.ListForNodeAsync(Proj, "plan", id)).Should().BeEmpty();
	}

	[Fact]
	public async Task Node_OnPostCommentEdit_UpdatesBody()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var added = await _comments.AddAsync(Proj, "plan", id, null, "t", "old text", null);

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentEditAsync(added.Id!, "new text", added.CurrentVersion, default);

		result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/ui/ws/{Proj}/tasks/plan/n");
		var thread = await _comments.ListForNodeAsync(Proj, "plan", id);
		thread.Should().ContainSingle().Which.Body.Should().Be("new text");
	}

	[Fact]
	public async Task Node_OnPostCommentEdit_StaleVersion_RerendersWithError_AndLeavesCommentUnchanged()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var added = await _comments.AddAsync(Proj, "plan", id, null, "t", "old text", null);

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentEditAsync(added.Id!, "new text", added.CurrentVersion + 5, default);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		var thread = await _comments.ListForNodeAsync(Proj, "plan", id);
		thread.Should().ContainSingle().Which.Body.Should().Be("old text");
	}

	[Fact]
	public async Task Node_OnPostCommentDelete_RemovesComment()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var added = await _comments.AddAsync(Proj, "plan", id, null, "t", "gone soon", null);

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentDeleteAsync(added.Id!, default);

		result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be($"/ui/ws/{Proj}/tasks/plan/n");
		(await _comments.ListForNodeAsync(Proj, "plan", id)).Should().BeEmpty();
	}

	[Fact]
	public async Task Node_OnPostCommentDelete_WithActiveReplies_RerendersWithError_AndKeepsBoth()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var root = await _comments.AddAsync(Proj, "plan", id, null, "t", "root", null);
		await _comments.AddAsync(Proj, "plan", id, root.Id, "t", "child", null);

		var page = NodePage();
		page.NodeId = id;
		var result = await page.OnPostCommentDeleteAsync(root.Id!, default);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		(await _comments.ListForNodeAsync(Proj, "plan", id)).Should().HaveCount(2);
	}

	[Fact]
	public async Task Node_OnPostCommentAdd_FeatureDisabled_ReturnsNotFound()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var page = NodePage(tasks: false);
		page.NodeId = NodeId("plan", "n");
		(await page.OnPostCommentAddAsync(null, "x", default)).Should().BeOfType<NotFoundResult>();
	}

	// ── TaskBoardModel (board page, many node cards) ────────────────────────────

	[Fact]
	public async Task Board_OnPostCommentAdd_AddsCommentUnderNamedNode_AndRedirectsToBoard()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");

		var page = BoardPage("plan");
		var result = await page.OnPostCommentAddAsync(id, parentId: null, body: "board remark", default);

		result.Should().BeOfType<RedirectToPageResult>();
		var thread = await _comments.ListForNodeAsync(Proj, "plan", id);
		thread.Should().ContainSingle().Which.Body.Should().Be("board remark");
	}

	[Fact]
	public async Task Board_OnPostCommentEdit_UpdatesBody()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var added = await _comments.AddAsync(Proj, "plan", id, null, "t", "old", null);

		var page = BoardPage("plan");
		var result = await page.OnPostCommentEditAsync(added.Id!, "edited", added.CurrentVersion, default);

		result.Should().BeOfType<RedirectToPageResult>();
		(await _comments.ListForNodeAsync(Proj, "plan", id)).Single().Body.Should().Be("edited");
	}

	[Fact]
	public async Task Board_OnPostCommentDelete_RemovesComment()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var added = await _comments.AddAsync(Proj, "plan", id, null, "t", "gone", null);

		var page = BoardPage("plan");
		var result = await page.OnPostCommentDeleteAsync(added.Id!, default);

		result.Should().BeOfType<RedirectToPageResult>();
		(await _comments.ListForNodeAsync(Proj, "plan", id)).Should().BeEmpty();
	}

	[Fact]
	public async Task Board_OnPostCommentDelete_WithActiveReplies_RerendersBoardWithError()
	{
		await Upsert("plan", new NodePatch { Key = "n", Title = "N", Body = "b" });
		var id = NodeId("plan", "n");
		var root = await _comments.AddAsync(Proj, "plan", id, null, "t", "root", null);
		await _comments.AddAsync(Proj, "plan", id, root.Id, "t", "child", null);

		var page = BoardPage("plan");
		var result = await page.OnPostCommentDeleteAsync(root.Id!, default);

		result.Should().BeOfType<PageResult>();
		page.Error.Should().NotBeNullOrEmpty();
		(await _comments.ListForNodeAsync(Proj, "plan", id)).Should().HaveCount(2);
	}

	[Fact]
	public async Task Board_OnPostCommentAdd_UnknownBoard_ReturnsNotFound()
	{
		var page = BoardPage("no-such-board");
		(await page.OnPostCommentAddAsync("whatever", null, "x", default)).Should().BeOfType<NotFoundResult>();
	}
}
