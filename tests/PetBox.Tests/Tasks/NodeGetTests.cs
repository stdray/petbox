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

namespace PetBox.Tests.Tasks;

// tasks_node_get — the addressed single-node read (token economy: one full node instead of
// re-fetching a whole board): `node` is a slug or a 32-hex NodeId, terminal statuses are
// returned like any other (an addressed ask has no includeClosed), and a miss is a clear
// board-naming error. Plus the tasks_search `status` filter: only the named slugs, with a
// terminal slug honored even when includeClosed=false.
public sealed class NodeGetTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public NodeGetTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-nodeget-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
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
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	[Fact]
	public async Task NodeGet_BySlug_AndByNodeId_ReturnsFullNode()
	{
		var http = Http();
		var body = new string('x', 2000);
		var nodes = McpInputs.NodesJson(
			$$"""[{"key":"parent","status":"Todo","title":"P"},{"key":"leaf","status":"Todo","title":"Leaf","body":"{{body}}","partOf":"parent","tags":["area:tasks"]}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b", nodes);

		var bySlug = await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "b", "leaf");
		bySlug.Board.Should().Be("b");
		bySlug.Node.Key.Should().Be("leaf");
		bySlug.Node.Title.Should().Be("Leaf");
		bySlug.Node.Body.Should().Be(body); // the COMPLETE body, never truncated
		bySlug.Node.ParentSlug.Should().Be("parent");
		bySlug.Node.Depth.Should().Be(1);
		bySlug.Node.Tags.Should().Contain("area:tasks");
		bySlug.Node.Version.Should().BeGreaterThan(0);
		bySlug.Node.Url.Should().BeNull(); // includeUrl off by default

		// The same node addressed by its 32-hex NodeId.
		var byId = await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "b", bySlug.Node.NodeId);
		byId.Node.Key.Should().Be("leaf");
		byId.Node.NodeId.Should().Be(bySlug.Node.NodeId);

		// includeUrl: the canonical slug permalink.
		var withUrl = await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "b", "leaf", includeUrl: true);
		withUrl.Node.Url.Should().Be($"https://box.test/ui/ws/{Proj}/tasks/b/leaf");
	}

	[Fact]
	public async Task NodeGet_TerminalStatusNode_IsReturned()
	{
		var http = Http();
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"key":"done-one","status":"Todo","title":"D","body":"finished work"}]"""));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"key":"done-one","status":"Done","version":1}]"""));

		// The default board read hides the terminal node…
		(await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b"))
			.Nodes.Should().BeEmpty();

		// …but the ADDRESSED read returns it regardless of terminality.
		var got = await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "b", "done-one");
		got.Node.Status.Should().Be("Done");
		got.Node.Body.Should().Be("finished work");
	}

	[Fact]
	public async Task NodeGet_MissingNode_ThrowsErrorNamingTheBoard()
	{
		var http = Http();
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"key":"real","status":"Todo","title":"R"}]"""));

		var missing = () => TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "b", "ghost");
		(await missing.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*ghost*").WithMessage("*board 'b'*");

		// A NodeId that resolves onto ANOTHER board is refused with both board names.
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "other",
			McpInputs.NodesJson("""[{"key":"elsewhere","status":"Todo","title":"E"}]"""));
		var otherId = (await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "other", "elsewhere")).Node.NodeId;
		var wrongBoard = () => TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "b", otherId);
		(await wrongBoard.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*board 'other'*").WithMessage("*'b'*");
	}

	[Fact]
	public async Task TasksGet_StatusFilter_ReturnsOnlyRequestedStatuses()
	{
		var http = Http();
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b", McpInputs.NodesJson(
			"""
			[{"key":"t1","status":"Todo","title":"T1"},
			 {"key":"t2","status":"Todo","title":"T2"},
			 {"key":"w1","status":"InProgress","title":"W1"}]
			"""));

		var only = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b", status: ["InProgress"]);
		only.Nodes.Select(n => n.Key).Should().Equal("w1");

		// Case-insensitive, multiple slugs.
		var both = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b", status: ["todo", "inprogress"]);
		both.Nodes.Select(n => n.Key).Should().BeEquivalentTo("t1", "t2", "w1");
	}

	[Fact]
	public async Task TasksGet_TerminalStatusInFilter_ReturnedWithoutIncludeClosed()
	{
		var http = Http();
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b", McpInputs.NodesJson(
			"""[{"key":"open-one","status":"Todo","title":"O"},{"key":"closed-one","status":"Todo","title":"C"}]"""));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"key":"closed-one","status":"Done","version":1}]"""));

		// Naming the terminal status is the explicit ask — no includeClosed needed.
		var done = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b", status: ["Done"]);
		done.Nodes.Select(n => n.Key).Should().Equal("closed-one");
		done.Nodes.Single().Status.Should().Be("Done");
	}

	[Fact]
	public async Task TasksGet_UnknownStatusSlug_SilentlyDropped()
	{
		await _tasks.UpsertAsync(Proj, "b", new[] { new NodePatch { Key = "n", Title = "N", Body = "" } });

		// An unknown status is silently dropped (soft filter); an all-unknown set → an empty result.
		var res = await _tasks.GetAsync(Proj, "b", status: ["bogus"]);
		res.Nodes.Should().BeEmpty();
	}
}
