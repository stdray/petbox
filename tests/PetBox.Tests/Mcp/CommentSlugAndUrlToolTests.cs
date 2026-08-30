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

namespace PetBox.Tests.Mcp;

// The two things work `comment-slug-and-refs` adds to the MCP comments surface, exercised through
// the adapter (the door an agent actually uses):
//   * `slug` on comments_upsert — the ONLY write door for a comment's human-readable address;
//   * `includeUrl` on comments_get / comments_search — the field the client report named as missing
//     ("comments_search and comments_get return id, nodeId, author, tags, version — but no url,
//     unlike nodes"), so a segmented document can quote a link to its own segment.
public sealed class CommentSlugAndUrlToolTests : IDisposable
{
	const string Proj = "proj";
	const string Board = "work";

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;
	readonly CommentService _comments;

	public CommentSlugAndUrlToolTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-comment-slug-mcp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
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

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	// A request context with a real scheme+host: `includeUrl` builds an ABSOLUTE url off the current
	// request, exactly as tasks_node_get's does, so a context without one would silently answer null.
	static IHttpContextAccessor Http()
	{
		var identity = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext
		{
			RequestServices = TestProjectCatalog.Services,
			User = new ClaimsPrincipal(identity),
		};
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.example");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	async Task<string> NodeAsync(string key)
	{
		await _tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = key, Title = "T", Body = "b" }]);
		return (await _tasks.GetNodeBySlugAsync(Proj, Board, key))!.Node.NodeId;
	}

	Task<CommentsUpsertResult> Upsert(IHttpContextAccessor http, params CommentItemInput[] items) =>
		CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, Board, items);

	[Fact]
	public async Task Upsert_CarriesTheSlug_ThroughTheAdapter_AndEchoesIt()
	{
		var http = Http();
		var node = await NodeAsync("the-article");

		var r = await Upsert(http, new CommentItemInput { Node = "the-article", Author = "alice", Body = "segment", Slug = "part-04" });

		r.Applied.Should().BeTrue();
		r.Added.Single().Slug.Should().Be("part-04");
		(await _comments.ListForNodeAsync(Proj, Board, node)).Single().Slug.Should().Be("part-04");
	}

	[Fact]
	public async Task Get_WithIncludeUrl_ReturnsAnAbsolutePermalink_AnchoredOnTheComment()
	{
		var http = Http();
		await NodeAsync("the-article");
		var created = await Upsert(http, new CommentItemInput { Node = "the-article", Author = "alice", Body = "segment", Slug = "part-04" });
		var id = created.Added.Single().Id;
		var nodeId = created.Added.Single().NodeId;

		var got = await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id, includeUrl: true);

		got.Url.Should().Be($"https://box.example/ui/ws/{Proj}/tasks/node/{nodeId}#comment-{id}");
		got.Slug.Should().Be("part-04");
	}

	[Fact]
	public async Task Get_WithoutIncludeUrl_OmitsTheUrl_LikeEveryOtherOptInField()
	{
		var http = Http();
		await NodeAsync("the-article");
		var id = (await Upsert(http, new CommentItemInput { Node = "the-article", Author = "alice", Body = "segment" }))
			.Added.Single().Id;

		(await CommentTools.GetAsync(http, Flags(), _comments, _tasks, Proj, id)).Url.Should().BeNull();
	}

	[Fact]
	public async Task Search_WithIncludeUrl_PutsAPermalinkOnEveryRow()
	{
		var http = Http();
		await NodeAsync("the-article");
		await Upsert(http,
			new CommentItemInput { Node = "the-article", Author = "alice", Body = "one", Slug = "part-one" },
			new CommentItemInput { Node = "the-article", Author = "bob", Body = "two" });

		var list = await CommentTools.SearchAsync(http, Flags(), _comments, _tasks, Proj,
			board: Board, node: "the-article", includeUrl: true);

		list.Items.Should().HaveCount(2);
		list.Items.Should().OnlyContain(c => c.Url!.StartsWith("https://box.example/ui/ws/") && c.Url.Contains("#comment-"));
		list.Items.Should().Contain(c => c.Slug == "part-one")
			.And.Contain(c => c.Slug == null, "a comment without an address is a normal row, not an omission");
	}
}
