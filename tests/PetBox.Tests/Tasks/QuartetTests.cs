using System.Security.Claims;
using System.Text.Json;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
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
		var en = Json(await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj));
		en.GetProperty("enabled").GetBoolean().Should().BeTrue();
		en.GetProperty("boards").EnumerateArray().Select(b => b.GetProperty("kind").GetString())
			.Should().Equal("intake", "ideas", "spec", "work"); // pipeline order

		// work board auto-wired to the spec board.
		var boards = Json(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).GetProperty("boards");
		var work = boards.EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "work");
		work.GetProperty("specBoard").GetString().Should().Be("spec");

		// Idempotent: a rerun keeps exactly four methodology boards.
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		Json(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).GetProperty("boards")
			.EnumerateArray().Count().Should().Be(4);
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
	public async Task Singleton_FreeBoards_Unlimited()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f1", "free");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f2", "free");
		Json(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).GetProperty("boards")
			.EnumerateArray().Count().Should().Be(2);
	}

	[Fact]
	public async Task MethodologyGet_IsCompactIndex_WithBodyLen_AndBoardFilter()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);

		var body = new string('x', 500);
		var nodes = JsonSerializer.Deserialize<JsonElement>(
			$$"""[{"key":"idea-a","status":"raw","type":"idea","title":"A","body":"{{body}}","tags":["area:tasks"]}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", nodes);

		// Default: an INDEX — the node carries tags/status/title but the body is sliced to null;
		// the board exposes a status histogram.
		var idx = Json(await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj));
		var ideas = idx.GetProperty("boards").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "ideas");
		ideas.GetProperty("counts").GetProperty("raw").GetInt32().Should().Be(1);
		var nodeA = ideas.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "idea-a");
		nodeA.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);        // no body by default
		nodeA.GetProperty("title").GetString().Should().Be("A");
		nodeA.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Contain("area:tasks"); // tags ALWAYS

		// bodyLen: the first N chars + "…" when cut.
		var sliced = Json(await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, bodyLen: 300))
			.GetProperty("boards").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "ideas")
			.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "idea-a")
			.GetProperty("body").GetString()!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");

		// includeBoards: only the requested quartet boards, in pipeline order.
		var only = Json(await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, includeBoards: ["spec", "ideas"]));
		only.GetProperty("boards").EnumerateArray().Select(b => b.GetProperty("kind").GetString())
			.Should().Equal("ideas", "spec");
	}

	[Fact]
	public async Task MethodologyGet_IncludeUrl_AddsAbsolutePermalink()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyEnableAsync(http, Flags(), _tasks, Proj);
		var nodes = JsonSerializer.Deserialize<JsonElement>("""[{"key":"idea-u","status":"raw","type":"idea","title":"U"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", nodes);

		// off by default: url is null.
		var off = Json(await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj))
			.GetProperty("boards").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "ideas")
			.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "idea-u");
		off.GetProperty("url").ValueKind.Should().Be(JsonValueKind.Null);

		// includeUrl: canonical slug permalink = base + /ui/{ws}/{project}/tasks/{board}/{slug}.
		var on = Json(await TasksTools.MethodologyGetAsync(http, Flags(), _tasks, Proj, includeUrl: true))
			.GetProperty("boards").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "ideas")
			.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "idea-u");
		on.GetProperty("url").GetString().Should().Be($"https://box.test/ui/ws/{Proj}/tasks/ideas/idea-u");
	}

	[Fact]
	public async Task Upsert_IncludeUrl_ReturnsPermalinkForCreatedNode()
	{
		var http = Http("tasks:read,tasks:write");
		var nodes = JsonSerializer.Deserialize<JsonElement>("""[{"key":"a","status":"Pending","title":"A"}]""");
		var added = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "free1", nodes, includeUrl: true))
			.GetProperty("added").EnumerateArray().Single();
		added.GetProperty("url").GetString().Should().Be($"https://box.test/ui/ws/{Proj}/tasks/free1/a");
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

	// spec echo-compact-by-default: the write-echo is a compact cursor advance — it carries
	// key/status/title/version but NOT the body unless bodyLen > 0. Defuses the footgun where a
	// stale sinceVersion re-dumps every recently-changed node's full body (the main context sink).
	[Fact]
	public async Task Upsert_EchoOmitsBodyByDefault_SlicesWithBodyLen_AndStaleCursorStaysBodiless()
	{
		var http = Http("tasks:read,tasks:write");
		var big = new string('y', 500);

		// Default echo: title present, body sliced to null (no re-dump of what I just sent).
		var nodesA = JsonSerializer.Deserialize<JsonElement>(
			$$"""[{"key":"a","status":"Pending","title":"A","body":"{{big}}"}]""");
		var resA = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesA));
		var addedA = resA.GetProperty("added").EnumerateArray().Single();
		addedA.GetProperty("title").GetString().Should().Be("A");
		addedA.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);

		// A second node with the DEFAULT stale cursor (sinceVersion = 0) echoes BOTH nodes
		// (version > 0), but every echoed body is still null — the dump is bodiless.
		var nodesB = JsonSerializer.Deserialize<JsonElement>(
			"""[{"key":"b","status":"Pending","title":"B","body":"zzz"}]""");
		var resB = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesB));
		var echoed = resB.GetProperty("added").EnumerateArray()
			.Concat(resB.GetProperty("updated").EnumerateArray()).ToList();
		echoed.Select(n => n.GetProperty("key").GetString()).Should().Contain(["a", "b"]); // stale cursor re-echoes 'a'
		echoed.Should().OnlyContain(n => n.GetProperty("body").ValueKind == JsonValueKind.Null);

		// bodyLen > 0: the opt-in sliced body — first N chars + "…" when cut.
		var nodesC = JsonSerializer.Deserialize<JsonElement>(
			$$"""[{"key":"c","status":"Pending","title":"C","body":"{{big}}"}]""");
		var sliced = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesC, bodyLen: 300))
			.GetProperty("added").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "c")
			.GetProperty("body").GetString()!;
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
		var nodes = JsonSerializer.Deserialize<JsonElement>(
			$$"""[{"key":"n","status":"Pending","title":"N","body":"{{big}}"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "g", nodes);

		// Default: the full body.
		var full = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g"))
			.GetProperty("nodes").EnumerateArray().Single().GetProperty("body").GetString()!;
		full.Length.Should().Be(500);

		// bodyLen > 0: first N chars + "…".
		var snip = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g", bodyLen: 100))
			.GetProperty("nodes").EnumerateArray().Single().GetProperty("body").GetString()!;
		snip.Length.Should().Be(101);
		snip.Should().EndWith("…");
	}

	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
