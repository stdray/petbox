using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// uniform-node-refs: every surface that takes a node reference accepts the SAME slug-or-NodeId
// format. blockedBy (tasks_upsert) resolves a slug on the same board and the `blocks` edge
// always carries a NodeId; relations_create/list resolve slugs across ALL boards (no board
// param) with an "ambiguous slug … boards: […]" error when a slug lives on 2+ boards;
// comments_upsert/search resolve a slug on their `board` param. 32-hex values are always NodeIds
// (passthrough — the pre-existing NodeId paths are the regression baseline).
public sealed class UniformNodeRefTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly RelationStore _relations;
	readonly CommentService _comments;
	readonly TasksService _tasks;

	public UniformNodeRefTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-noderef-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_relations = new RelationStore(_db);
		_comments = new CommentService(_factory);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), _relations, new TagStore(_factory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http(string scopes = "tasks:read,tasks:write")
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	// Upsert nodes onto a board and return key -> NodeId of the call's echo.
	async Task<Dictionary<string, string>> Seed(IHttpContextAccessor http, string board, string nodesJson)
	{
		var r = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, board, McpInputs.NodesJson(nodesJson));
		r.Applied.Should().BeTrue();
		return r.Added.Concat(r.Updated).ToDictionary(n => n.Key, n => n.NodeId, StringComparer.Ordinal);
	}

	// ---- blockedBy (tasks_upsert): slug resolves on the SAME board, edge carries a NodeId ----

	[Fact]
	public async Task BlockedBy_Slug_ResolvesOnBoard_EdgeCarriesNodeId()
	{
		var http = Http();
		var ids = await Seed(http, "b", """[{"key":"blocker","status":"Todo","title":"B"}]""");
		await Seed(http, "b", """[{"key":"task-x","status":"Todo","title":"X","blockedBy":"blocker"}]""");

		// The blocks edge binds the blocker's stable NodeId, never the raw slug.
		var edges = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, "blocker");
		var edge = edges.Relations.Single(r => r.Kind == "blocks");
		edge.FromNodeId.Should().Be(ids["blocker"]);
		edge.FromNodeId.Should().MatchRegex("^[0-9a-f]{32}$");

		// And the enriched read surfaces the link.
		var view = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b");
		view.Nodes.Single(n => n.Key == "task-x").BlockedBy!.Single().NodeId.Should().Be(ids["blocker"]);
	}

	[Fact]
	public async Task BlockedBy_Slug_SameBatchBlocker_Resolves()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"first","status":"Todo","title":"F"},
			 {"key":"second","status":"Todo","title":"S","blockedBy":"first"}]
			""");
		var edges = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, "second");
		edges.Relations.Single(r => r.Kind == "blocks").FromNodeId.Should().Be(ids["first"]);
	}

	[Fact]
	public async Task BlockedBy_UnknownSlug_RejectedNamingTheBoard()
	{
		var http = Http();
		var act = () => TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"key":"task-x","status":"Todo","title":"X","blockedBy":"ghost"}]"""));
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*blockedBy 'ghost'*")
			.WithMessage("*node 'task-x'*")
			.WithMessage("*does not match any node on board 'b'*")
			.WithMessage("*NodeId*");
	}

	[Fact]
	public async Task BlockedBy_NodeId_StillWorks()
	{
		var http = Http();
		var ids = await Seed(http, "b", """[{"key":"blocker","status":"Todo","title":"B"}]""");
		await Seed(http, "b", $$"""[{"key":"task-y","status":"Todo","title":"Y","blockedBy":"{{ids["blocker"]}}"}]""");

		var view = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b");
		view.Nodes.Single(n => n.Key == "task-y").BlockedBy!.Single().NodeId.Should().Be(ids["blocker"]);
	}

	// ---- relations_create/list: slug resolves across ALL boards, ambiguity is an error ----

	[Fact]
	public async Task RelationsCreate_SlugsBothSides_ResolveToNodeIds()
	{
		var http = Http();
		var b1 = await Seed(http, "b1", """[{"key":"alpha","status":"Todo","title":"A"}]""");
		var b2 = await Seed(http, "b2", """[{"key":"beta","status":"Todo","title":"B"}]""");

		// Cross-board: each side resolves project-wide from its slug alone.
		var rel = await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, kind: "blocks", fromNodeId: "alpha", toNodeId: "beta");
		rel.Relations.Should().ContainSingle();
		rel.Relations[0].FromNodeId.Should().Be(b1["alpha"]);
		rel.Relations[0].ToNodeId.Should().Be(b2["beta"]);
	}

	[Fact]
	public async Task RelationsCreate_NodeIds_StillWork()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"one","status":"Todo","title":"1"},{"key":"two","status":"Todo","title":"2"}]
			""");
		var rel = await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, kind: "blocks", fromNodeId: ids["one"], toNodeId: ids["two"]);
		rel.Relations.Should().ContainSingle();
		rel.Relations[0].FromNodeId.Should().Be(ids["one"]);
		rel.Relations[0].ToNodeId.Should().Be(ids["two"]);
	}

	[Fact]
	public async Task RelationsCreate_AmbiguousSlug_ErrorListsBoards()
	{
		var http = Http();
		await Seed(http, "b1", """[{"key":"dup","status":"Todo","title":"D1"},{"key":"target","status":"Todo","title":"T"}]""");
		await Seed(http, "b2", """[{"key":"dup","status":"Todo","title":"D2"}]""");

		// Single-form error is verbatim (no items[i] prefix) — the pre-batch wire text is preserved.
		var act = () => RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, kind: "blocks", fromNodeId: "dup", toNodeId: "target");
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*ambiguous slug 'dup'*")
			.WithMessage("*boards: [b1, b2]*")
			.WithMessage("*pass the node's NodeId*");
	}

	[Fact]
	public async Task RelationsCreate_UnknownSlug_Rejected()
	{
		var http = Http();
		await Seed(http, "b", """[{"key":"real","status":"Todo","title":"R"}]""");

		var act = () => RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, kind: "blocks", fromNodeId: "ghost", toNodeId: "real");
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage($"*node 'ghost' does not match any active node in project '{Proj}'*");
	}

	[Fact]
	public async Task RelationsList_BySlug_ReturnsTheNodesEdges()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"one","status":"Todo","title":"1"},{"key":"two","status":"Todo","title":"2"}]
			""");
		await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, kind: "blocks", fromNodeId: ids["one"], toNodeId: ids["two"]);

		// Listed by slug and by NodeId identically (the uniform ref).
		var bySlug = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, "one");
		var byId = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, ids["one"]);
		bySlug.Relations.Should().BeEquivalentTo(byId.Relations);
		bySlug.Relations.Single().FromNodeId.Should().Be(ids["one"]);
	}

	// ---- relations_create/delete batch form ----

	[Fact]
	public async Task RelationsCreate_BatchItems_CreatesAll()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"a","status":"Todo","title":"A"},{"key":"b","status":"Todo","title":"B"},
			 {"key":"c","status":"Todo","title":"C"},{"key":"d","status":"Todo","title":"D"}]
			""");

		var created = await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, items:
		[
			new RelationCreateItemInput { Kind = "blocks", From = "a", To = "b" },
			new RelationCreateItemInput { Kind = "relates_to", FromNodeId = ids["c"], ToNodeId = ids["d"] },
		]);

		created.Relations.Should().HaveCount(2);
		created.Relations[0].Kind.Should().Be("blocks");
		created.Relations[0].FromNodeId.Should().Be(ids["a"]);
		created.Relations[0].ToNodeId.Should().Be(ids["b"]);
		created.Relations[1].Kind.Should().Be("relates_to");
		created.Relations[1].FromNodeId.Should().Be(ids["c"]);
		created.Relations[1].ToNodeId.Should().Be(ids["d"]);
	}

	[Fact]
	public async Task RelationsCreate_Batch_ValidationFailsWholeBatch_NoPartialWrite()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"a","status":"Todo","title":"A"},{"key":"b","status":"Todo","title":"B"}]
			""");

		var act = () => RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, items:
		[
			new RelationCreateItemInput { Kind = "blocks", From = "a", To = "b" },
			new RelationCreateItemInput { Kind = "blocks", From = "ghost", To = "b" },
		]);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*items[1]*")
			.WithMessage("*ghost*");

		// First item was validated but never written (fail-all-before-create).
		var list = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, ids["a"]);
		list.Relations.Should().BeEmpty();
	}

	[Fact]
	public async Task RelationsCreate_ItemsPlusSingleForm_Rejected()
	{
		var http = Http();
		await Seed(http, "b", """[{"key":"a","status":"Todo","title":"A"},{"key":"b","status":"Todo","title":"B"}]""");

		var act = () => RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj,
			kind: "blocks", fromNodeId: "a", toNodeId: "b",
			items: [new RelationCreateItemInput { Kind = "blocks", From = "a", To = "b" }]);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*either items*")
			.WithMessage("*not both*");
	}

	[Fact]
	public async Task RelationsDelete_BatchAndSingleForm()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"a","status":"Todo","title":"A"},{"key":"b","status":"Todo","title":"B"},
			 {"key":"c","status":"Todo","title":"C"},{"key":"d","status":"Todo","title":"D"}]
			""");
		var created = await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, items:
		[
			new RelationCreateItemInput { Kind = "blocks", From = "a", To = "b" },
			new RelationCreateItemInput { Kind = "blocks", From = "c", To = "d" },
		]);
		var id0 = created.Relations[0].Id;
		var id1 = created.Relations[1].Id;

		var single = await RelationTools.DeleteAsync(http, Flags(), _relations, Proj, id: id0);
		single.Relations.Should().ContainSingle();
		single.Relations[0].Id.Should().Be(id0);
		single.Relations[0].Deleted.Should().BeTrue();

		var batch = await RelationTools.DeleteAsync(http, Flags(), _relations, Proj, ids: [id1, "no-such"]);
		batch.Relations.Should().HaveCount(2);
		batch.Relations[0].Should().Be(new RelationDeletedResult(id1, true));
		batch.Relations[1].Should().Be(new RelationDeletedResult("no-such", false));
	}

	// ---- comments_upsert/search: slug resolves on the `board` param ----

	static CommentItemInput NewComment(string node, string author, string body) =>
		new() { NodeId = node, Author = author, Body = body };

	[Fact]
	public async Task CommentsCreate_And_List_BySlug()
	{
		var http = Http();
		var ids = await Seed(http, "b", """[{"key":"talky","status":"Todo","title":"T"}]""");

		var add = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "b", [NewComment("talky", "alice", "hello")]);
		add.Applied.Should().BeTrue();

		// The thread binds the node's stable NodeId; slug and NodeId list the same thread.
		var bySlug = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: "b", nodeId: "talky");
		bySlug.Items.Single().NodeId.Should().Be(ids["talky"]);
		var byId = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: "b", nodeId: ids["talky"]);
		byId.Items.Should().BeEquivalentTo(bySlug.Items);
	}

	[Fact]
	public async Task CommentsAdd_UnknownSlug_RejectedNamingTheBoard()
	{
		var http = Http();
		await Seed(http, "b", """[{"key":"real","status":"Todo","title":"R"}]""");

		var act = () => CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "b", [NewComment("ghost", "alice", "hi")]);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*node 'ghost' does not match any active node on board 'b'*");

		// A slug that lives on ANOTHER board doesn't leak in — comments are board-scoped: a node
		// not on this board yields an EMPTY result (soft read), not an error.
		await Seed(http, "other", """[{"key":"elsewhere","status":"Todo","title":"E"}]""");
		var wrongBoard = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: "b", nodeId: "elsewhere");
		wrongBoard.Items.Should().BeEmpty();
	}

	// ---- WATERMARK over the MCP surface: an echoed currentVersion is the next call's baseline ----

	// tasks_upsert: the board `currentVersion` from one call's echo is a valid baseline for the
	// next — even above the edited node's own version (a sibling advanced the cursor). A baseline
	// above the board cursor is a FutureBaseline conflict (a cursor from another board/scope).
	[Fact]
	public async Task Upsert_EchoCurrentVersion_IsValidNextBaseline_FutureRejected()
	{
		var http = Http();
		await Seed(http, "b", """[{"key":"a","status":"Todo","title":"A"}]"""); // v1
		var second = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"key":"z","status":"Todo","title":"Z"}]""")); // v2 -> board cursor
		var cursor = second.CurrentVersion;

		// Edit 'a' (own version 1) with the board cursor as baseline — the watermark accepts it.
		var edit = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson($$"""[{"key":"a","status":"Todo","title":"A-edited","version":{{cursor}}}]"""));
		edit.Applied.Should().BeTrue();
		edit.Conflicts.Should().BeEmpty();

		// A baseline above the board cursor is a wrong-scope quote -> FutureBaseline.
		var future = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson($$"""[{"key":"a","status":"Todo","title":"A3","version":{{cursor + 500}}}]"""));
		future.Applied.Should().BeFalse();
		future.Conflicts.Should().ContainSingle(c => c.Kind == "FutureBaseline");
	}

	// comments_upsert (PATCH): same watermark over the thread cursor.
	[Fact]
	public async Task CommentEdit_ThreadCurrentVersion_IsValidNextBaseline_FutureRejected()
	{
		var http = Http();
		await Seed(http, "b", """[{"key":"talky","status":"Todo","title":"T"}]""");
		var c1 = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "b", [NewComment("talky", "alice", "first")]);  // v1
		var c2 = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "b", [NewComment("talky", "bob", "second")]);   // v2 -> thread cursor
		var c1Id = c1.Added.Single().Id;
		var cursor = c2.CurrentVersion;

		// Edit c1 (own version 1) with the thread cursor as baseline — accepted.
		var edit = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "b",
			[new CommentItemInput { Id = c1Id, Body = "first-edited", Version = cursor }]);
		edit.Applied.Should().BeTrue();
		edit.Conflicts.Should().BeEmpty();

		// Above the thread cursor -> FutureBaseline, teaching Reason surfaced.
		var future = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "b",
			[new CommentItemInput { Id = c1Id, Body = "x", Version = cursor + 500 }]);
		future.Applied.Should().BeFalse();
		var conflict = future.Conflicts.Single();
		conflict.Kind.Should().Be("FutureBaseline");
		conflict.Reason.Should().Contain("another board/scope");
	}
}
