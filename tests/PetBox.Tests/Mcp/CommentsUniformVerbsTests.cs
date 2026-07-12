using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// The comments family on the uniform-entity-verbs matrix (comments_upsert / _search / _delta /
// _get), exercised through the MCP adapter over a real per-project TasksDb (FTS included, so the
// lexical q-search path runs for real). Mirrors tasks/memory: create + patch batch, list = search
// without q, a lexical query, a version-cursor delta, and the addressed single read.
public sealed class CommentsUniformVerbsTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "ideas";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _tasksFactory;
	readonly TasksService _tasks;
	readonly CommentService _comments;

	public CommentsUniformVerbsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-comments-verbs-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_comments = new CommentService(_tasksFactory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _tasksFactory), new RelationStore(_tasksFactory),
			new TagStore(_tasksFactory), _comments);
	}

	public void Dispose()
	{
		_db.Dispose();
		_tasksFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity(
			[new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Tasks"] = "true" }).Build());

	static CommentItemInput Create(string node, string author, string body, string[]? tags = null) =>
		new() { NodeId = node, Author = author, Body = body, Tags = tags };

	Task<CommentsUpsertResult> Upsert(IHttpContextAccessor http, params CommentItemInput[] items) =>
		CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, Board, items);

	// A stable NodeId to hang comments on. A 32-hex value passes through node-ref resolution
	// unresolved (uniform-node-refs), so no board node is needed for these thread tests.
	static string NewNode() => Guid.NewGuid().ToString("N");

	[Fact]
	public async Task Upsert_Create_Then_Patch_EchoesOnlyThisCall()
	{
		var http = Http();
		var node = NewNode();

		var created = await Upsert(http, Create(node, "alice", "first body", ["artifact:plan"]));
		created.Applied.Should().BeTrue();
		created.Added.Should().ContainSingle();
		created.Updated.Should().BeEmpty();
		var id = created.Added.Single().Id;
		created.Added.Single().Tags.Should().Equal("artifact:plan");

		// PATCH the body under the echoed cursor; tags omitted → left as-is.
		var patched = await Upsert(http, new CommentItemInput { Id = id, Body = "edited body", Version = created.CurrentVersion });
		patched.Applied.Should().BeTrue();
		patched.Added.Should().BeEmpty();
		patched.Updated.Should().ContainSingle(c => c.Id == id);       // echo covers ONLY this call
		patched.CurrentVersion.Should().BeGreaterThan(created.CurrentVersion);

		var got = await CommentTools.GetAsync(http, Flags(), _comments, Proj, id, bodyLen: -1);
		got.Body.Should().Be("edited body");
		got.Tags.Should().Equal("artifact:plan");                       // survived the tags-omitted patch
	}

	[Fact]
	public async Task Upsert_StaleVersion_Conflicts_NothingWritten()
	{
		var http = Http();
		var node = NewNode();
		var id = (await Upsert(http, Create(node, "alice", "v1"))).Added.Single().Id;
		await Upsert(http, new CommentItemInput { Id = id, Body = "v2", Version = 1 }); // advances the comment

		// A baseline of 0 is now stale (the comment moved past it) → a conflict, nothing written.
		var stale = await Upsert(http, new CommentItemInput { Id = id, Body = "clobber", Version = 0 });
		stale.Applied.Should().BeFalse();
		stale.Updated.Should().BeEmpty();
		stale.Conflicts.Should().ContainSingle(c => c.Id == id && c.Kind == "Stale");

		(await CommentTools.GetAsync(http, Flags(), _comments, Proj, id, bodyLen: -1)).Body.Should().Be("v2");
	}

	[Fact]
	public async Task Search_List_WithoutQuery_IsChronological()
	{
		var http = Http();
		var node = NewNode();
		await Upsert(http, Create(node, "a", "alpha comment"));
		await Upsert(http, Create(node, "b", "bravo comment"));

		var res = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, board: Board, nodeId: node);
		res.Retrievers.Should().BeNull(); // a listing carries no retrieval provenance
		res.Items.Select(c => c.Body).Should().Equal("alpha comment", "bravo comment"); // chronological
	}

	[Fact]
	public async Task Search_WithQuery_IsLexical_AndDegradesWithoutSemantic()
	{
		var http = Http();
		var node = NewNode();
		await Upsert(http, Create(node, "a", "the vector index cursor rebuild"));
		await Upsert(http, Create(node, "b", "an unrelated grocery list"));

		var res = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj, q: "vector cursor");
		res.Items.Should().ContainSingle();
		res.Items.Single().Body.Should().Contain("vector index cursor");
		// Documented degrade: comments have no semantic leg yet → lexical floor only.
		res.Retrievers.Should().NotBeNull();
		res.Retrievers!.Lexical.Should().BeTrue();
		res.Retrievers.Semantic.Should().BeFalse();
		res.Retrievers.Degraded.Should().BeFalse();
	}

	[Fact]
	public async Task Delta_ReturnsChangesSinceCursor()
	{
		var http = Http();
		var node = NewNode();
		var first = await Upsert(http, Create(node, "a", "first"));
		var cursor = first.CurrentVersion;

		await Upsert(http, Create(node, "b", "second"));
		await Upsert(http, Create(node, "c", "third"));

		var delta = await CommentTools.DeltaAsync(http, Flags(), _comments, Proj, Board, cursor, bodyLen: -1);
		delta.Added.Select(c => c.Body).Should().BeEquivalentTo(["second", "third"]); // only post-cursor
		delta.CurrentVersion.Should().BeGreaterThan(cursor);
	}

	[Fact]
	public async Task Get_MissingId_IsError()
	{
		var http = Http();
		var act = () => CommentTools.GetAsync(http, Flags(), _comments, Proj, "no-such-comment");
		await act.Should().ThrowAsync<InvalidOperationException>();
	}
}
