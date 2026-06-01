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
		await TasksTools.BoardCreateAsync(http, Flags(), _store, Proj, "roadmap", null);

		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { phase = "logging", status = "InProgress", name = "Logging", body = "winston -> PetBox", priority = 0 },
			new { phase = "logging", wave = "ingest", status = "Pending", name = "Ingest", body = "ship CLEF", priority = 1 },
			new { phase = "logging", wave = "ingest", task = "endpoint", status = "Pending", name = "Endpoint", body = "POST endpoint", priority = 2 },
		});
		await TasksTools.UpsertAsync(http, Flags(), _store, _relations, Proj, "roadmap", nodes);

		var got = Json(await TasksTools.GetAsync(http, Flags(), _store, Proj, "roadmap"));
		var arr = got.GetProperty("nodes").EnumerateArray().ToList();
		arr.Should().HaveCount(3);

		var leaf = arr.Single(n => n.GetProperty("depth").GetInt32() == 3);
		leaf.GetProperty("key").GetString().Should().Be("logging/ingest/endpoint");
		leaf.GetProperty("phase").GetString().Should().Be("logging");
		leaf.GetProperty("wave").GetString().Should().Be("ingest");
		leaf.GetProperty("task").GetString().Should().Be("endpoint");
		leaf.GetProperty("parentKey").GetString().Should().Be("logging/ingest");
		leaf.GetProperty("name").GetString().Should().Be("Endpoint");
		leaf.GetProperty("body").GetString().Should().Be("POST endpoint");

		var phase = arr.Single(n => n.GetProperty("depth").GetInt32() == 1);
		phase.GetProperty("key").GetString().Should().Be("logging");
		phase.GetProperty("parentKey").ValueKind.Should().Be(JsonValueKind.Null);
	}

	[Fact]
	public async Task Upsert_InvalidSegment_IsRejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _store, Proj, "roadmap", null);

		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { phase = "Bad Phase", status = "Pending", body = "x" },
		});
		// GuardAsync surfaces the validation failure as a structured error result
		// (not a thrown, opaque MCP error).
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _store, _relations, Proj, "roadmap", nodes));
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
			new { phase = "alpha", status = "Pending", name = "Alpha", body = "do alpha", priority = 0 },
		});
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _store, _relations, Proj, "fresh", nodes));

		(await _store.ExistsAsync(Proj, "fresh")).Should().BeTrue();

		// F4: the added node in the upsert delta carries `name` (matches the
		// documented contract and tasks.get), so a client merge won't drop titles.
		var added = res.GetProperty("added").EnumerateArray().Single();
		added.GetProperty("name").GetString().Should().Be("Alpha");
	}

	[Fact]
	public async Task Upsert_AcceptsNodesAsJsonString()
	{
		var http = Http("tasks:read,tasks:write");
		// Real MCP clients pass the untyped `nodes` param as a JSON *string*, not an
		// array element — the upsert must accept that (regression for D6).
		var arrayJson = """[{"phase":"alpha","name":"Alpha","status":"Pending","body":"b","priority":0}]""";
		var nodesAsString = JsonSerializer.SerializeToElement(arrayJson); // ValueKind == String
		var res = Json(await TasksTools.UpsertAsync(http, Flags(), _store, _relations, Proj, "strboard", nodesAsString));
		res.GetProperty("added").EnumerateArray().Should().ContainSingle()
			.Which.GetProperty("name").GetString().Should().Be("Alpha");
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

	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o);
}
