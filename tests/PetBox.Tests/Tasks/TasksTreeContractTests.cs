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

// Verifies the flat-node + part_of tree contract surfaced by the Tasks MCP tools:
// nodes are flat slugs, vertical structure is the part_of edge, and tasks.get returns
// parentNodeId/parentSlug + a computed depth (the projection that replaced l1/l2/l3).
[Collection("DataModule")]
public sealed class TasksTreeContractTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly CommentService _commentSvc;
	readonly TasksService _tasks;

	public TasksTreeContractTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-taskstree-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_relations = new RelationStore(_db);
		_commentSvc = new CommentService(_factory);
		_tasks = new TasksService(_store, _relations, new TagStore(_factory), _commentSvc);
	}

	// Spec writes require an `accepted` idea (ideaRef). Drive one through the gate and return
	// its NodeId.
	async Task<string> AcceptedIdeaId(IHttpContextAccessor http)
	{
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "ideas", "ideas");
		var idea = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "drv", type = "idea", status = "exploring", body = "x" } })));
		var ideaId = idea.GetProperty("added")[0].GetProperty("nodeId").GetString()!;
		await CommentTools.AddAsync(http, Flags(), _commentSvc, Proj, "ideas", ideaId, "t", "plan", parentId: null, tags: new[] { "artifact:spec_plan" });
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "drv", type = "idea", status = "review", version = 1 } }));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "drv", type = "idea", status = "accepted", version = 2 } }));
		return ideaId;
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Upsert_FlatNodesWithPartOf_DecomposeViaEdges()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "roadmap", null);

		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "logging", status = "InProgress", title = "Logging", body = "winston -> PetBox", priority = 0 },
			new { key = "ingest", partOf = "logging", status = "Pending", title = "Ingest", body = "ship CLEF", priority = 1 },
			new { key = "endpoint", partOf = "ingest", status = "Pending", title = "Endpoint", body = "POST endpoint", priority = 2 },
		});
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap", nodes);

		var got = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "roadmap"));
		var arr = got.GetProperty("nodes").EnumerateArray().ToList();
		arr.Should().HaveCount(3);

		// Depth is computed from part_of (root = 0). The leaf is two edges down.
		var leaf = arr.Single(n => n.GetProperty("depth").GetInt32() == 2);
		leaf.GetProperty("key").GetString().Should().Be("endpoint");
		leaf.GetProperty("parentSlug").GetString().Should().Be("ingest");
		leaf.GetProperty("title").GetString().Should().Be("Endpoint");
		leaf.GetProperty("body").GetString().Should().Be("POST endpoint");

		var root = arr.Single(n => n.GetProperty("depth").GetInt32() == 0);
		root.GetProperty("key").GetString().Should().Be("logging");
		root.GetProperty("parentNodeId").ValueKind.Should().Be(JsonValueKind.Null);
	}

	[Fact]
	public async Task PartOf_Reparent_SingleParent_AndCycleRejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "roadmap", null);
		var up = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap",
			JsonSerializer.SerializeToElement(new object[]
			{
				new { key = "a", status = "InProgress", title = "A", body = "x" },
				new { key = "b", partOf = "a", status = "InProgress", title = "B", body = "x" },
				new { key = "c", status = "InProgress", title = "C", body = "x" },
			})));
		var ver = up.GetProperty("currentVersion").GetInt64();

		FieldOf(Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "roadmap")), "b", "parentSlug").Should().Be("a");

		// Reparent b under c (single active parent — the a->b edge is closed).
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap",
			JsonSerializer.SerializeToElement(new object[] { new { key = "b", partOf = "c", version = ver } }));
		FieldOf(Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "roadmap")), "b", "parentSlug").Should().Be("c");

		// Cycle: a part_of b, but b is already a descendant of c which... make a a child of b
		// while b is reachable — a->b->? a is root, b under c. Set c part_of b → c,b,? Let's
		// make the direct 2-cycle: set a part_of b is fine (a root). Instead force a cycle:
		// b under c; now set c part_of b → c->b->c? b's parent is c, so c part_of b loops.
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap",
			JsonSerializer.SerializeToElement(new object[] { new { key = "c", partOf = "b", version = ver } })));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
		res.GetProperty("error").GetProperty("message").GetString().Should().Contain("cycle");
	}

	// Find a node by flat key in a tasks.get result and read a string field.
	static string FieldOf(JsonElement got, string key, string field) =>
		got.GetProperty("nodes").EnumerateArray()
			.Single(n => n.GetProperty("key").GetString() == key)
			.GetProperty(field).GetString()!;

	[Fact]
	public async Task GroupBy_TagNamespace_BucketsNodes_NoneLast()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "g", null);
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "g",
			JsonSerializer.SerializeToElement(new object[]
			{
				new { key = "a", status = "Pending", title = "A", body = "x", tags = new[] { "area:ui", "concern:security" } },
				new { key = "b", status = "Pending", title = "B", body = "x", tags = new[] { "area:ui" } },
				new { key = "c", status = "Pending", title = "C", body = "x", tags = new[] { "area:llm" } },
			}));

		// group-by area: ui {a,b}, llm {c}. groupBy echoes the ordered dimension list.
		var byArea = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g", groupBy: "area"));
		byArea.GetProperty("groupBy").EnumerateArray().Select(d => d.GetString()).Should().Equal("area");
		var areaGroups = byArea.GetProperty("groups").EnumerateArray()
			.ToDictionary(g => g.GetProperty("key").GetString()!, g => g.GetProperty("nodeKeys").EnumerateArray().Select(k => k.GetString()).ToList());
		areaGroups["area:ui"].Should().BeEquivalentTo(["a", "b"]);
		areaGroups["area:llm"].Should().BeEquivalentTo(["c"]);

		// group-by concern: security {a}, and the untagged b,c fall into "(none)" — listed last.
		var byConcern = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g", groupBy: "concern"));
		var keys = byConcern.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("key").GetString()).ToList();
		keys.Should().Equal("concern:security", "(none)"); // (none) last
	}

	[Fact]
	public async Task GroupBy_OrderedMultiKey_NestsAndPreservesMultimembership()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "g", null);
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "g",
			JsonSerializer.SerializeToElement(new object[]
			{
				// a is in TWO areas → multimembership: it appears under both area:ui and area:llm.
				new { key = "a", status = "Pending", title = "A", body = "x", tags = new[] { "area:ui", "area:llm", "concern:security" } },
				new { key = "b", status = "Pending", title = "B", body = "x", tags = new[] { "area:ui" } }, // no concern → "(none)"
			}));

		// groupBy [area, concern]: top level = area buckets, each split by concern, leaves = nodeKeys.
		var got = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g", groupBy: "area, concern"));
		got.GetProperty("groupBy").EnumerateArray().Select(d => d.GetString()).Should().Equal("area", "concern");

		var areas = got.GetProperty("groups").EnumerateArray()
			.ToDictionary(g => g.GetProperty("key").GetString()!, g => g);
		areas.Keys.Should().BeEquivalentTo(["area:ui", "area:llm"]); // a multimember → both

		// area:ui nests concern:security {a} then "(none)" {b} (none last); inner groups are leaves.
		var uiSub = areas["area:ui"].GetProperty("subGroups").EnumerateArray()
			.Select(g => (key: g.GetProperty("key").GetString()!, nodes: g.GetProperty("nodeKeys").EnumerateArray().Select(k => k.GetString()).ToList()))
			.ToList();
		uiSub.Select(s => s.key).Should().Equal("concern:security", "(none)");
		uiSub.Single(s => s.key == "concern:security").nodes.Should().BeEquivalentTo(["a"]);
		uiSub.Single(s => s.key == "(none)").nodes.Should().BeEquivalentTo(["b"]);

		// area:llm holds only a, under concern:security.
		var llmSub = areas["area:llm"].GetProperty("subGroups").EnumerateArray().Single();
		llmSub.GetProperty("key").GetString().Should().Be("concern:security");
		llmSub.GetProperty("nodeKeys").EnumerateArray().Select(k => k.GetString()).Should().BeEquivalentTo(["a"]);
		// non-leaf (area) groups carry no nodeKeys — those live on the leaf level.
		areas["area:ui"].GetProperty("nodeKeys").EnumerateArray().Should().BeEmpty();
	}

	[Fact]
	public async Task Supersedes_RecordsEdge_AndObsoletesOldNode()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "spec", "spec");
		var ir = await AcceptedIdeaId(http);
		// new supersedes old (both in one batch): old is moved to the spec terminal-cancel.
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "spec",
			JsonSerializer.SerializeToElement(new object[]
			{
				new { key = "old", status = "defined", title = "Old req", body = "x", ideaRef = ir },
				new { key = "new", status = "defined", title = "New req", body = "x", supersedes = "old", ideaRef = ir },
			}));

		var got = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "spec", includeClosed: true));
		var nodes = got.GetProperty("nodes").EnumerateArray().ToList();
		// old obsoleted → moved to the spec workflow's terminal-cancel (deprecated).
		nodes.Single(n => n.GetProperty("key").GetString() == "old").GetProperty("status").GetString().Should().Be("deprecated");
		// new carries a supersedes link to old.
		var newNode = nodes.Single(n => n.GetProperty("key").GetString() == "new");
		newNode.GetProperty("supersedes").EnumerateArray().Single().GetProperty("slug").GetString().Should().Be("old");
	}

	[Fact]
	public async Task GroupBy_UnknownNamespace_Rejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "g", null);
		var res = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "g", groupBy: "status"));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
	}

	[Fact]
	public async Task Upsert_InvalidSegment_IsRejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "roadmap", null);

		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { l1 = "Bad Phase", status = "Pending", body = "x" },
		});
		// GuardAsync surfaces the validation failure as a structured error result
		// (not a thrown, opaque MCP error).
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap", nodes));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
	}

	[Fact]
	public async Task Upsert_AutoVivifiesBoard_AndDeltaCarriesName()
	{
		var http = Http("tasks:read,tasks:write");

		// No BoardCreate first — a cold upsert must auto-create the board (F2), so
		// following the agent guide literally no longer throws.
		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { l1 = "alpha", status = "Pending", title = "Alpha", body = "do alpha", priority = 0 },
		});
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "fresh", nodes));

		(await _store.ExistsAsync(Proj, "fresh")).Should().BeTrue();

		// F4: the added node in the upsert delta carries `title` (matches the
		// documented contract and tasks.get), so a client merge won't drop titles.
		var added = res.GetProperty("added").EnumerateArray().Single();
		added.GetProperty("title").GetString().Should().Be("Alpha");
	}

	[Fact]
	public async Task Upsert_AcceptsNodesAsJsonString()
	{
		var http = Http("tasks:read,tasks:write");
		// Real MCP clients pass the untyped `nodes` param as a JSON *string*, not an
		// array element — the upsert must accept that (regression for D6).
		var arrayJson = """[{"l1":"alpha","title":"Alpha","status":"Pending","body":"b","priority":0}]""";
		var nodesAsString = JsonSerializer.SerializeToElement(arrayJson); // ValueKind == String
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "strboard", nodesAsString));
		res.GetProperty("added").EnumerateArray().Should().ContainSingle()
			.Which.GetProperty("title").GetString().Should().Be("Alpha");
	}

	[Fact]
	public async Task Upsert_ChangingType_IsRejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "b", null);
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", type = "alpha", status = "todo", body = "x" } }), 0);

		// Editing the node to a different type must fail — type is immutable once set.
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", type = "beta", version = 1, body = "x" } })));
		res.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
		res.GetProperty("error").GetProperty("message").GetString().Should().Contain("immutable");

		// Editing other fields while keeping the type (or omitting it) still works.
		var ok = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", version = 1, body = "edited" } })));
		ok.GetProperty("applied").GetBoolean().Should().BeTrue();
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	// Mirror the MCP boundary (camelCase policy) so typed-record results read like live JSON.
	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
