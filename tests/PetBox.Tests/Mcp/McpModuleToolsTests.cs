using System.Security.Claims;
using System.Text.Json;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Sessions.Data;
using PetBox.Tasks.Data;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// Exercises the tasks.*/memory.*/session.* tool methods directly (mocked
// HttpContext + real stores). The MCP transport itself is covered by the
// existing McpDataToolsTests; here we validate tool logic, auth guards, and
// the temporal integration.
[Collection("DataModule")]
public sealed class McpModuleToolsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _tasksFactory;
	readonly ScopedDbFactory<MemoryDb> _memFactory;
	readonly ScopedDbFactory<SessionsDb> _sessFactory;
	readonly TaskBoardStore _boards;
	readonly RelationStore _relations;
	readonly MemoryStore _stores;
	readonly SessionStore _sessions;

	public McpModuleToolsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mcptools-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });

		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_memFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_sessFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);

		_boards = new TaskBoardStore(_db, _tasksFactory);
		_relations = new RelationStore(_db);
		_stores = new MemoryStore(_db, _memFactory);
		_sessions = new SessionStore(_sessFactory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_tasksFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_sessFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Tasks_Create_Upsert_Get_Roundtrip()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _boards, Proj, "roadmap");

		var nodes = JsonSerializer.SerializeToElement(new[]
		{
			new { key = "phase-16", status = "InProgress", body = "Data", priority = 100 },
			new { key = "phase-16/wave-1", status = "Done", body = "Foundation", priority = 200 },
		});
		var up = Json(await TasksTools.UpsertAsync(http, Flags(), _boards, _relations, Proj, "roadmap", nodes, 0));
		up.GetProperty("applied").GetBoolean().Should().BeTrue();
		up.GetProperty("inserted").GetInt32().Should().Be(2);

		var get = Json(await TasksTools.GetAsync(http, Flags(), _boards, _relations, Proj, "roadmap", includeClosed: true));
		var keys = get.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("key").GetString()).ToList();
		keys.Should().Equal("phase-16", "phase-16/wave-1"); // priority order
	}

	[Fact]
	public async Task Tasks_StaleUpsert_ReturnsConflict()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _boards, Proj, "b");
		await TasksTools.UpsertAsync(http, Flags(), _boards, _relations, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", status = "Pending", body = "v1" } }), 0);
		await TasksTools.UpsertAsync(http, Flags(), _boards, _relations, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", status = "Done", body = "byB", version = 1 } }), 0);
		var r = Json(await TasksTools.UpsertAsync(http, Flags(), _boards, _relations, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", status = "Done", body = "byA", version = 1 } }), 0));

		r.GetProperty("applied").GetBoolean().Should().BeFalse();
		r.GetProperty("conflicts").EnumerateArray().Should().ContainSingle();
		r.GetProperty("conflicts")[0].GetProperty("kind").GetString().Should().Be("Stale");
	}

	[Fact]
	public async Task Tasks_Rename_ShowsLineage()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _boards, Proj, "b");
		await TasksTools.UpsertAsync(http, Flags(), _boards, _relations, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "old", status = "Done", body = "x" } }), 0);
		await TasksTools.UpsertAsync(http, Flags(), _boards, _relations, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "new", status = "Done", body = "x", version = 1, prevKey = "old" } }), 0);

		var get = Json(await TasksTools.GetAsync(http, Flags(), _boards, _relations, Proj, "b", includeClosed: true));
		var node = get.GetProperty("nodes").EnumerateArray().Single();
		node.GetProperty("key").GetString().Should().Be("new");
		node.GetProperty("renamedFrom").EnumerateArray().Select(x => x.GetString()).Should().Equal("old");
	}

	[Fact]
	public async Task Tasks_MissingScope_Throws()
	{
		var http = Http("tasks:read");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardCreateAsync(http, Flags(), _boards, Proj, "b"));
	}

	[Fact]
	public async Task Tasks_FeatureOff_Throws()
	{
		var http = Http("tasks:read,tasks:write");
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			TasksTools.BoardCreateAsync(http, Flags(tasks: false), _boards, Proj, "b"));
	}

	[Fact]
	public async Task Tasks_CrossProjectKey_Authorizes_NormalKeyForOtherProjectRejected()
	{
		// A cross-project key (project="*") may operate on any project...
		var star = Http("tasks:read,tasks:write", project: "*");
		await TasksTools.BoardCreateAsync(star, Flags(), _boards, Proj, "x");
		Json(await TasksTools.GetAsync(star, Flags(), _boards, _relations, Proj, "x"))
			.GetProperty("kind").GetString().Should().Be("free");

		// ...while a key scoped to a different project is rejected for this one.
		var other = Http("tasks:read,tasks:write", project: "other");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardListAsync(other, Flags(), _boards, Proj));
	}

	[Fact]
	public async Task Memory_Upsert_Search_Roundtrip()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.StoreCreateAsync(http, Flags(), _stores, Proj, "notes");
		await MemoryTools.UpsertAsync(http, Flags(), _stores, Proj, "notes",
			JsonSerializer.SerializeToElement(new[]
			{
				new { key = "go", type = "reference", description = "Go style", body = "tabs not spaces", tags = "go,style" },
			}), 0);

		var hits = Json(await MemoryTools.SearchAsync(http, Flags(), _stores, Proj, "notes", "tabs"));
		hits.GetProperty("entries").EnumerateArray().Should().ContainSingle();
	}

	[Fact]
	public async Task Session_Append_Get_List()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessions, Proj, "s1", "claude-code", "# plan");

		var got = Json(await SessionTools.GetAsync(http, Flags(), _sessions, Proj, "s1"));
		got.GetProperty("content").GetString().Should().Be("# plan");

		var list = Json(await SessionTools.ListAsync(http, Flags(), _sessions, Proj));
		list.GetProperty("sessions").EnumerateArray().Should().ContainSingle();
	}

	static IHttpContextAccessor Http(string scopes, string? project = null)
	{
		var id = new ClaimsIdentity([new Claim("project", project ?? Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags(bool tasks = true, bool memory = true)
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = tasks.ToString(),
			["Features:Memory"] = memory.ToString(),
		}).Build();
		return new FeatureFlags(cfg);
	}

	// Mirror the MCP boundary, which serialises tool results with the camelCase policy
	// (so typed-record results read the same as the live JSON: NodeId -> "nodeId").
	static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
	static JsonElement Json(object? o) => JsonSerializer.SerializeToElement(o, CamelCase);
}
