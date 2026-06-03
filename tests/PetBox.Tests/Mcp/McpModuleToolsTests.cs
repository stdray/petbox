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
using PetBox.Memory.Services;
using PetBox.Sessions.Services;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
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
	readonly TasksService _tasks;
	readonly MemoryStore _stores;
	readonly MemoryService _memory;
	readonly SessionService _sessionSvc;
	readonly SessionStore _sessions;
	readonly CommentService _commentSvc;

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
		_tasks = new TasksService(_boards, _relations, new TagStore(_tasksFactory), new CommentService(_tasksFactory));
		_stores = new MemoryStore(_db, _memFactory);
		_memory = new MemoryService(_stores);
		_sessions = new SessionStore(_sessFactory);
		_sessionSvc = new SessionService(_sessions);
		_commentSvc = new CommentService(_tasksFactory);
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
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "roadmap");

		var nodes = JsonSerializer.SerializeToElement(new object[]
		{
			new { key = "phase-16", status = "InProgress", body = "Data", priority = 100 },
			new { key = "wave-1", partOf = "phase-16", status = "Done", body = "Foundation", priority = 200 },
		});
		var up = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap", nodes, 0));
		up.GetProperty("applied").GetBoolean().Should().BeTrue();
		up.GetProperty("inserted").GetInt32().Should().Be(2);

		var get = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "roadmap", includeClosed: true));
		var keys = get.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("key").GetString()).ToList();
		keys.Should().Equal("phase-16", "wave-1"); // priority order
	}

	[Fact]
	public async Task Tasks_StaleUpsert_ReturnsConflict()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "b");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", status = "Pending", body = "v1" } }), 0);
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", status = "Done", body = "byB", version = 1 } }), 0);
		var r = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "n", status = "Done", body = "byA", version = 1 } }), 0));

		r.GetProperty("applied").GetBoolean().Should().BeFalse();
		r.GetProperty("conflicts").EnumerateArray().Should().ContainSingle();
		r.GetProperty("conflicts")[0].GetProperty("kind").GetString().Should().Be("Stale");
	}

	[Fact]
	public async Task Tasks_Rename_ShowsLineage()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "b");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "old", status = "Done", body = "x" } }), 0);
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			JsonSerializer.SerializeToElement(new[] { new { key = "new", status = "Done", body = "x", version = 1, prevKey = "old" } }), 0);

		var get = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "b", includeClosed: true));
		var node = get.GetProperty("nodes").EnumerateArray().Single();
		node.GetProperty("key").GetString().Should().Be("new");
		node.GetProperty("renamedFrom").EnumerateArray().Select(x => x.GetString()).Should().Equal("old");
	}

	[Fact]
	public async Task Tasks_MissingScope_Throws()
	{
		var http = Http("tasks:read");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "b"));
	}

	[Fact]
	public async Task Tasks_FeatureOff_Throws()
	{
		var http = Http("tasks:read,tasks:write");
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			TasksTools.BoardCreateAsync(http, Flags(tasks: false), _tasks, Proj, "b"));
	}

	[Fact]
	public async Task Tasks_CrossProjectKey_Authorizes_NormalKeyForOtherProjectRejected()
	{
		// A cross-project key (project="*") may operate on any project...
		var star = Http("tasks:read,tasks:write", project: "*");
		await TasksTools.BoardCreateAsync(star, Flags(), _tasks, Proj, "x");
		Json(await TasksTools.GetAsync(star, Flags(), _tasks, Proj, "x"))
			.GetProperty("kind").GetString().Should().Be("free");

		// ...while a key scoped to a different project is rejected for this one.
		var other = Http("tasks:read,tasks:write", project: "other");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardListAsync(other, Flags(), _tasks, Proj));
	}

	[Fact]
	public async Task Memory_Upsert_Search_Roundtrip()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.StoreCreateAsync(http, Flags(), _memory, Proj, "notes");
		await MemoryTools.UpsertAsync(http, Flags(), _memory, Proj, "notes",
			JsonSerializer.SerializeToElement(new[]
			{
				new { key = "go", type = "reference", description = "Go style", body = "tabs not spaces", tags = "go,style" },
			}), 0);

		var hits = Json(await MemoryTools.SearchAsync(http, Flags(), _memory, Proj, "notes", "tabs"));
		hits.GetProperty("entries").EnumerateArray().Should().ContainSingle();
	}

	[Fact]
	public async Task Session_Append_Get_List()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "s1", "claude-code", "# plan");

		var got = Json(await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s1"));
		got.GetProperty("content").GetString().Should().Be("# plan");

		var list = Json(await SessionTools.ListAsync(http, Flags(), _sessionSvc, Proj));
		list.GetProperty("sessions").EnumerateArray().Should().ContainSingle();
	}

	[Fact]
	public async Task Comments_Add_Reply_List_DeleteWithChildrenRejected()
	{
		var http = Http("tasks:read,tasks:write");
		var add = Json(await CommentTools.AddAsync(http, Flags(), _commentSvc, Proj, "ideas", "node-1", "alice", "root body", parentId: null, tags: new[] { "artifact:plan" }));
		add.GetProperty("applied").GetBoolean().Should().BeTrue();
		var id = add.GetProperty("id").GetString()!;

		await CommentTools.AddAsync(http, Flags(), _commentSvc, Proj, "ideas", "node-1", "bob", "a reply", parentId: id, tags: null);

		var list = Json(await CommentTools.ListAsync(http, Flags(), _commentSvc, Proj, "ideas", "node-1"));
		var rows = list.GetProperty("comments").EnumerateArray().ToList();
		rows.Should().HaveCount(2);
		rows.Single(c => c.GetProperty("id").GetString() == id).GetProperty("tags").EnumerateArray()
			.Select(t => t.GetString()).Should().Equal("artifact:plan");

		// Deleting a parent with an active reply → GuardAsync surfaces a structured error.
		var del = Json(await CommentTools.DeleteAsync(http, Flags(), _commentSvc, Proj, "ideas", id));
		del.TryGetProperty("error", out _).Should().BeTrue();
	}

	[Fact]
	public async Task Comments_MissingWriteScope_SurfacesError()
	{
		var http = Http("tasks:read"); // no tasks:write
		var r = Json(await CommentTools.AddAsync(http, Flags(), _commentSvc, Proj, "ideas", "n", "a", "b", parentId: null, tags: null));
		r.GetProperty("error").GetProperty("type").GetString().Should().Be("UnauthorizedAccessException");
	}

	[Fact]
	public async Task Idea_ReviewGate_RequiresSpecPlan_ThenAcceptable()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "ideas", "ideas");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "idea-x", type = "idea", status = "exploring", body = "x" } }), 0);

		var node = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "ideas"))
			.GetProperty("nodes").EnumerateArray().Single();
		var nodeId = node.GetProperty("nodeId").GetString()!;
		var v = node.GetProperty("version").GetInt64();

		// exploring -> review WITHOUT a spec_plan artifact: rejected by the gate (tasks.upsert
		// wraps the body in GuardAsync, so the failure surfaces as a structured error).
		var blocked = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "idea-x", type = "idea", status = "review", version = v } }), 0));
		blocked.GetProperty("error").GetProperty("type").GetString().Should().Be("InvalidOperationException");
		blocked.GetProperty("error").GetProperty("message").GetString().Should().Contain("spec_plan");

		// Add the spec_plan artifact, then the same transition applies.
		await CommentTools.AddAsync(http, Flags(), _commentSvc, Proj, "ideas", nodeId, "claude", "the plan", parentId: null, tags: new[] { "artifact:spec_plan" });
		var rev = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "idea-x", type = "idea", status = "review", version = v } }), 0));
		rev.GetProperty("applied").GetBoolean().Should().BeTrue();

		// review -> accepted (the maintainer gate; enforceApproval is off so it applies).
		var v2 = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "ideas"))
			.GetProperty("nodes").EnumerateArray().Single().GetProperty("version").GetInt64();
		var acc = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "idea-x", type = "idea", status = "accepted", version = v2 } }), 0));
		acc.GetProperty("applied").GetBoolean().Should().BeTrue();
	}

	[Fact]
	public async Task Idea_ExploringToAccepted_NoLongerAllowed_MustGoThroughReview()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "ideas", "ideas");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "idea-y", type = "idea", status = "exploring", body = "x" } }), 0);
		var v = Json(await TasksTools.GetAsync(http, Flags(), _tasks, Proj, "ideas"))
			.GetProperty("nodes").EnumerateArray().Single().GetProperty("version").GetInt64();
		// The direct exploring->accepted transition was removed; you must pass through review.
		var r = Json(await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			JsonSerializer.SerializeToElement(new[] { new { key = "idea-y", type = "idea", status = "accepted", version = v } }), 0));
		r.GetProperty("error").GetProperty("type").GetString().Should().Be("ArgumentException");
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
