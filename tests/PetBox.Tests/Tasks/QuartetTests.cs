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

// methodology-quartet: opt-in provisioning of the four singleton boards + auto-wiring
// work->spec, the one-per-project singleton guard, and the unified surface.
[Collection("DataModule")]
public sealed class QuartetTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public QuartetTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-quartet-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Enable_ProvisionsQuartet_AutoWires_AndIsIdempotent()
	{
		var http = Http("tasks:read,tasks:write");
		var en = await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		en.Enabled.Should().BeTrue();
		en.Boards.Select(b => b.Kind)
			.Should().Equal("intake", "ideas", "spec", "work"); // pipeline order

		// work board auto-wired to the spec board.
		var boards = (await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards;
		var work = boards.Single(b => b.Kind == "work");
		work.SpecBoard.Should().Be("spec");

		// Idempotent: a rerun keeps exactly four methodology boards.
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards
			.Count.Should().Be(4);
	}

	[Fact]
	public async Task Singleton_SecondBoardOfMethodologyKind_Rejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "spec", "spec");
		// A 2nd spec board is rejected (one-per-project); GuardAsync is not on board_create,
		// so the service throws — assert the message via the service directly.
		var act = () => _tasks.CreateBoardAsync(Proj, "spec2", "spec", null, null);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*one-per-project*");
	}

	[Fact]
	public async Task Singleton_SimpleBoards_Unlimited()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f1", "simple");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f2", "simple");
		(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards
			.Count.Should().Be(2);
	}

	[Fact]
	public async Task MethodologyGet_IsCompactIndex_WithBodyLen_AndBoardFilter()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);

		var body = new string('x', 500);
		var nodes = McpInputs.NodesJson(
			$$"""[{"key":"idea-a","status":"raw","type":"idea","title":"A","body":"{{body}}","tags":["area:tasks"]}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", nodes);

		// Default: an INDEX — the node carries tags/status/title but the body is sliced to null;
		// the board exposes a status histogram.
		var idx = await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj);
		var ideas = idx.Boards.Single(b => b.Kind == "ideas");
		ideas.Counts["raw"].Should().Be(1);
		var nodeA = ideas.Nodes.Single(n => n.Key == "idea-a");
		nodeA.Body.Should().BeNull();        // no body by default
		nodeA.Title.Should().Be("A");
		nodeA.Tags.Should().Contain("area:tasks"); // tags ALWAYS

		// Small boards fit the output budget: no truncation markers, no hint — as before.
		idx.Hint.Should().BeNull();
		ideas.Truncated.Should().BeNull();
		ideas.Omitted.Should().BeNull();

		// bodyLen: the first N chars + "…" when cut.
		var sliced = (await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, bodyLen: 300))
			.Boards.Single(b => b.Kind == "ideas")
			.Nodes.Single(n => n.Key == "idea-a")
			.Body!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");

		// includeBoards: only the requested quartet boards, in pipeline order.
		var only = await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, includeBoards: ["spec", "ideas"]);
		only.Boards.Select(b => b.Kind)
			.Should().Equal("ideas", "spec");
	}

	// spec bounded-result-sets / surface-economy: a board too large for the index budget is
	// prefix-cut with STRUCTURAL markers (truncated/omitted per board + a narrowing hint on
	// the view); the status histogram stays complete — the overview never lies about totals.
	[Fact]
	public async Task MethodologyGet_LargeBoard_CutsRowsWithMarkers_CountsStayComplete()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);

		const int total = 150;
		var title = new string('t', 200);
		var body = new string('b', 100);
		var rows = string.Join(",", Enumerable.Range(0, total).Select(i =>
			$$"""{"key":"idea-{{i}}","status":"raw","type":"idea","title":"{{title}}","body":"{{body}}"}"""));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", McpInputs.NodesJson($"[{rows}]"));

		var idx = await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj);
		var ideas = idx.Boards.Single(b => b.Kind == "ideas");

		// Histogram is ALWAYS complete — the cheap essence of the overview.
		ideas.Counts["raw"].Should().Be(total);

		// Rows are prefix-cut, and the cut is explicit: truncated + omitted add back up.
		ideas.Nodes.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		ideas.Truncated.Should().BeTrue();
		ideas.Omitted.Should().Be(total - ideas.Nodes.Count);

		// The response tells the caller how to narrow.
		idx.Hint.Should().NotBeNull();
		idx.Hint.Should().Contain("includeBoards").And.Contain("tasks.get");

		// bodyLen keeps working under the budget: every INCLUDED row carries its slice
		// ("…"-terminated), and fatter rows just mean fewer of them fit.
		var sliced = (await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, bodyLen: 50))
			.Boards.Single(b => b.Kind == "ideas");
		sliced.Truncated.Should().BeTrue();
		sliced.Nodes.Should().OnlyContain(n => n.Body != null && n.Body.Length == 51 && n.Body.EndsWith('…'));
		sliced.Nodes.Count.Should().BeLessThanOrEqualTo(ideas.Nodes.Count);
	}

	[Fact]
	public async Task MethodologyGet_IncludeUrl_AddsAbsolutePermalink()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		var nodes = McpInputs.NodesJson("""[{"key":"idea-u","status":"raw","type":"idea","title":"U"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", nodes);

		// off by default: url is null.
		var off = (await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj))
			.Boards.Single(b => b.Kind == "ideas")
			.Nodes.Single(n => n.Key == "idea-u");
		off.Url.Should().BeNull();

		// includeUrl: canonical slug permalink = base + /ui/{ws}/{project}/tasks/{board}/{slug}.
		var on = (await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, includeUrl: true))
			.Boards.Single(b => b.Kind == "ideas")
			.Nodes.Single(n => n.Key == "idea-u");
		on.Url.Should().Be($"https://box.test/ui/ws/{Proj}/tasks/ideas/idea-u");
	}

	[Fact]
	public async Task Upsert_IncludeUrl_ReturnsPermalinkForCreatedNode()
	{
		var http = Http("tasks:read,tasks:write");
		var nodes = McpInputs.NodesJson("""[{"key":"a","status":"Todo","title":"A"}]""");
		var added = (await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "free1", nodes, includeUrl: true))
			.Added.Single();
		added.Url.Should().Be($"https://box.test/ui/ws/{Proj}/tasks/free1/a");
	}

	[Fact]
	public async Task MethodologyGet_InvalidIncludeBoards_Rejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		// The MCP tool wraps errors via GuardAsync, so assert the message on the service directly.
		var act = () => _tasks.GetMethodologyAsync(Proj, includeBoards: ["bogus"]);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*not a quartet board*");
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(id) };
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

	// spec echo-compact-by-default: the write-echo is a compact ack — it carries
	// key/status/title/version but NOT the body unless bodyLen > 0. Defuses the footgun where
	// a write re-dumps every recently-changed node's full body (the main context sink).
	[Fact]
	public async Task Upsert_EchoOmitsBodyByDefault_SlicesWithBodyLen_AndDeltaStaysBodiless()
	{
		var http = Http("tasks:read,tasks:write");
		var big = new string('y', 500);

		// Default echo: title present, body sliced to null (no re-dump of what I just sent).
		var nodesA = McpInputs.NodesJson(
			$$"""[{"key":"a","status":"Todo","title":"A","body":"{{big}}"}]""");
		var resA = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesA);
		var addedA = resA.Added.Single();
		addedA.Title.Should().Be("A");
		addedA.Body.Should().BeNull();

		// A second node echoes only ITSELF (echo-covers-the-call, no cursor parameter on a
		// write); the full board delta is tasks.delta's job — and even that dump stays bodiless.
		var nodesB = McpInputs.NodesJson(
			"""[{"key":"b","status":"Todo","title":"B","body":"zzz"}]""");
		var resB = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesB);
		resB.Added.Concat(resB.Updated).Select(n => n.Key).Should().Equal("b");

		var resFull = await TasksTools.DeltaAsync(http, Flags(), _tasks, Proj, "ce", 0);
		var echoed = resFull.Added.Concat(resFull.Updated).ToList();
		echoed.Select(n => n.Key).Should().Contain(["a", "b"]); // the delta covers everyone
		echoed.Should().OnlyContain(n => n.Body == null);

		// bodyLen > 0: the opt-in sliced body — first N chars + "…" when cut.
		var nodesC = McpInputs.NodesJson(
			$$"""[{"key":"c","status":"Todo","title":"C","body":"{{big}}"}]""");
		var sliced = (await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesC, bodyLen: 300))
			.Added.Single(n => n.Key == "c")
			.Body!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");
	}

	// spec read-snippet-on-demand: tasks.get returns full bodies by default (the Razor board
	// needs them) but snippets each node body when bodyLen > 0 — the slice is MCP-adapter-only.
	[Fact]
	public async Task TasksGet_FullBodyByDefault_SnippetsWithBodyLen()
	{
		var http = Http("tasks:read,tasks:write");
		var big = new string('z', 500);
		var nodes = McpInputs.NodesJson(
			$$"""[{"key":"n","status":"Todo","title":"N","body":"{{big}}"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "g", nodes);

		// Default: the full body.
		var full = ((PlanBoardView)await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g"))
			.Nodes.Single().Body;
		full.Length.Should().Be(500);

		// bodyLen > 0: first N chars + "…".
		var snip = ((PlanBoardView)await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g", bodyLen: 100))
			.Nodes.Single().Body;
		snip.Length.Should().Be(101);
		snip.Should().EndWith("…");
	}
}
