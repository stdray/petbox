using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// uniform-node-refs: every surface that takes a node reference accepts the SAME slug-or-NodeId
// format. blockedBy (tasks.upsert) resolves a slug on the same board and the `blocks` edge
// always carries a NodeId; relations.create/list resolve slugs across ALL boards (no board
// param) with an "ambiguous slug … boards: […]" error when a slug lives on 2+ boards;
// comments.create/list resolve a slug on their `board` param. 32-hex values are always NodeIds
// (passthrough — the pre-existing NodeId paths are the regression baseline).
[Collection("DataModule")]
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
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
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

	// ---- blockedBy (tasks.upsert): slug resolves on the SAME board, edge carries a NodeId ----

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
		var view = (PlanBoardView)await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "b");
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

		var view = (PlanBoardView)await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "b");
		view.Nodes.Single(n => n.Key == "task-y").BlockedBy!.Single().NodeId.Should().Be(ids["blocker"]);
	}

	// ---- relations.create/list: slug resolves across ALL boards, ambiguity is an error ----

	[Fact]
	public async Task RelationsCreate_SlugsBothSides_ResolveToNodeIds()
	{
		var http = Http();
		var b1 = await Seed(http, "b1", """[{"key":"alpha","status":"Todo","title":"A"}]""");
		var b2 = await Seed(http, "b2", """[{"key":"beta","status":"Todo","title":"B"}]""");

		// Cross-board: each side resolves project-wide from its slug alone.
		var rel = await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, "blocks", "alpha", "beta");
		rel.FromNodeId.Should().Be(b1["alpha"]);
		rel.ToNodeId.Should().Be(b2["beta"]);
	}

	[Fact]
	public async Task RelationsCreate_NodeIds_StillWork()
	{
		var http = Http();
		var ids = await Seed(http, "b", """
			[{"key":"one","status":"Todo","title":"1"},{"key":"two","status":"Todo","title":"2"}]
			""");
		var rel = await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, "blocks", ids["one"], ids["two"]);
		rel.FromNodeId.Should().Be(ids["one"]);
		rel.ToNodeId.Should().Be(ids["two"]);
	}

	[Fact]
	public async Task RelationsCreate_AmbiguousSlug_ErrorListsBoards()
	{
		var http = Http();
		await Seed(http, "b1", """[{"key":"dup","status":"Todo","title":"D1"},{"key":"target","status":"Todo","title":"T"}]""");
		await Seed(http, "b2", """[{"key":"dup","status":"Todo","title":"D2"}]""");

		var act = () => RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, "blocks", "dup", "target");
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

		var act = () => RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, "blocks", "ghost", "real");
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
		await RelationTools.CreateAsync(http, Flags(), _relations, _tasks, Proj, "blocks", ids["one"], ids["two"]);

		// Listed by slug and by NodeId identically (the uniform ref).
		var bySlug = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, "one");
		var byId = await RelationTools.ListAsync(http, Flags(), _relations, _tasks, Proj, ids["one"]);
		bySlug.Relations.Should().BeEquivalentTo(byId.Relations);
		bySlug.Relations.Single().FromNodeId.Should().Be(ids["one"]);
	}

	// ---- comments.create/list: slug resolves on the `board` param ----

	[Fact]
	public async Task CommentsCreate_And_List_BySlug()
	{
		var http = Http();
		var ids = await Seed(http, "b", """[{"key":"talky","status":"Todo","title":"T"}]""");

		var add = await CommentTools.CreateAsync(http, Flags(), _comments, _tasks, Proj, "b", "talky", "alice", "hello");
		add.Applied.Should().BeTrue();

		// The thread binds the node's stable NodeId; slug and NodeId list the same thread.
		var bySlug = await CommentTools.ListAsync(http, Flags(), _comments, _tasks, Proj, "b", "talky");
		bySlug.Comments.Single().NodeId.Should().Be(ids["talky"]);
		var byId = await CommentTools.ListAsync(http, Flags(), _comments, _tasks, Proj, "b", ids["talky"]);
		byId.Comments.Should().BeEquivalentTo(bySlug.Comments);
	}

	[Fact]
	public async Task CommentsAdd_UnknownSlug_RejectedNamingTheBoard()
	{
		var http = Http();
		await Seed(http, "b", """[{"key":"real","status":"Todo","title":"R"}]""");

		var act = () => CommentTools.CreateAsync(http, Flags(), _comments, _tasks, Proj, "b", "ghost", "alice", "hi");
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*node 'ghost' does not match any active node on board 'b'*");

		// A slug that lives on ANOTHER board doesn't leak in — comments are board-scoped.
		await Seed(http, "other", """[{"key":"elsewhere","status":"Todo","title":"E"}]""");
		var wrongBoard = () => CommentTools.ListAsync(http, Flags(), _comments, _tasks, Proj, "b", "elsewhere");
		(await wrongBoard.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*node 'elsewhere' does not match any active node on board 'b'*");
	}
}
