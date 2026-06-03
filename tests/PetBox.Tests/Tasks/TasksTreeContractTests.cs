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

// Verifies the Phase>Wave>Task tree contract surfaced by the Tasks MCP tools:
// structured phase/wave/task input canonicalises to the engine Key, and tasks.get
// returns decomposed levels + depth + parentKey.
[Collection("DataModule")]
public sealed class TasksTreeContractTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly TasksService _tasks;

	public TasksTreeContractTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-taskstree-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_relations = new RelationStore(_db);
		_tasks = new TasksService(_store, _relations, new TagStore(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Upsert_StructuredLevels_CanonicalisesAndDecomposes()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "roadmap", null);

		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { l1 = "logging", status = "InProgress", title = "Logging", body = "winston -> PetBox", priority = 0 },
			new { l1 = "logging", l2 = "ingest", status = "Pending", title = "Ingest", body = "ship CLEF", priority = 1 },
			new { l1 = "logging", l2 = "ingest", l3 = "endpoint", status = "Pending", title = "Endpoint", body = "POST endpoint", priority = 2 },
		});
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap", nodes);

		var got = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "roadmap"));
		var arr = got.GetProperty("nodes").EnumerateArray().ToList();
		arr.Should().HaveCount(3);

		var leaf = arr.Single(n => n.GetProperty("depth").GetInt32() == 3);
		leaf.GetProperty("key").GetString().Should().Be("logging/ingest/endpoint");
		leaf.GetProperty("l1").GetString().Should().Be("logging");
		leaf.GetProperty("l2").GetString().Should().Be("ingest");
		leaf.GetProperty("l3").GetString().Should().Be("endpoint");
		leaf.GetProperty("parentKey").GetString().Should().Be("logging/ingest");
		leaf.GetProperty("title").GetString().Should().Be("Endpoint");
		leaf.GetProperty("body").GetString().Should().Be("POST endpoint");

		var root = arr.Single(n => n.GetProperty("depth").GetInt32() == 1);
		root.GetProperty("key").GetString().Should().Be("logging");
		root.GetProperty("parentKey").ValueKind.Should().Be(JsonValueKind.Null);
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
