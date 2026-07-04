using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Contract;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// The response budget on the remaining list-shaped reads (spec bounded-result-sets, the
// shared ResponseBudget helper): memory_search / session_search / comments_list are prefix-cut
// on the wire form of their rows when they outgrow the output budget and marked structurally
// (truncated:true + omitted + a narrowing hint) — never silently; an in-budget list
// serializes byte-identical to the old shape (the marker fields are null and omitted).
public sealed class ListBudgetTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _tasksFactory;
	readonly ScopedDbFactory<MemoryDb> _memFactory;
	readonly ScopedDbFactory<SessionsDb> _sessFactory;
	readonly TasksService _tasks;
	readonly MemoryService _memory;
	readonly SessionService _sessions;
	readonly CommentService _comments;

	public ListBudgetTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-listbudget-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });

		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_memFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_sessFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);

		_tasks = new TasksService(new TaskBoardStore(_db, _tasksFactory), new RelationStore(_db),
			new TagStore(_tasksFactory), new CommentService(_tasksFactory));
		_memory = new MemoryService(new MemoryStore(_db, _memFactory));
		_sessions = new SessionService(new SessionStore(_sessFactory));
		_comments = new CommentService(_tasksFactory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_tasksFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_memFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_sessFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity(
			[new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write,memory:read,memory:write")], "test");
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

	// The MCP wire shape (camelCase + null-omit) — what an agent actually receives.
	static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	// ---- memory_search (listing mode) ----

	async Task SeedMemoryAsync(int count, int bodyChars)
	{
		var body = new string('m', bodyChars);
		var entries = Enumerable.Range(0, count).Select(i => new MemoryEntryInput
		{
			Key = $"entry-{i:d3}",
			Version = 0,
			Type = "Project",
			Description = $"entry {i}",
			Body = body,
		}).ToList();
		await _memory.UpsertAsync(Proj, "notes", entries, []);
	}

	[Fact]
	public async Task MemoryList_Small_NoMarkers_WireShapeUnchanged()
	{
		await SeedMemoryAsync(3, 200);

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes");

		res.Items.Count.Should().Be(3);
		res.Truncated.Should().BeNull();
		res.Omitted.Should().BeNull();
		res.Hint.Should().BeNull();
		JsonSerializer.Serialize(res, Wire).Should().NotContainAny("truncated", "omitted", "hint");
	}

	[Fact]
	public async Task MemoryList_Large_PrefixCut_MarkersAndHint()
	{
		const int total = 40;
		await SeedMemoryAsync(total, 2000); // ~80k chars of bodies > the 30k budget

		// bodyLen:-1 = the full body (the default is now a compact snippet); full bodies overflow.
		var res = await MemoryTools.SearchAsync(Http(), Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", bodyLen: -1, limit: 0);

		res.Items.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		// Prefix-cut in listing order (one seed batch → equal Updated, ties on key) —
		// the head of the list, no holes.
		res.Items.Select(e => e.Key).Should().Equal(
			Enumerable.Range(0, res.Items.Count).Select(i => $"entry-{i:d3}"));
		res.Truncated.Should().BeTrue();
		res.Omitted.Should().Be(total - res.Items.Count);
		res.Hint.Should().ContainAll("type", "limit", "bodyLen", "memory_get");
	}

	[Fact]
	public async Task MemoryList_BodyLen_ShrinksRows_SoAllFit()
	{
		const int total = 40;
		await SeedMemoryAsync(total, 2000);

		var snipped = await MemoryTools.SearchAsync(Http(), Flags(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", bodyLen: 20, limit: 0);

		snipped.Items.Count.Should().Be(total);
		snipped.Truncated.Should().BeNull();
	}

	// ---- session_search (listing mode — the former session.list) ----

	[Fact]
	public async Task SessionList_Small_NoMarkers()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", [new SessionMessageInput("session", "x")]);

		var res = await SessionTools.SearchAsync(Http(), Flags(), _sessions, null!, Proj);

		res.Items.Should().ContainSingle();
		res.Truncated.Should().BeNull();
		JsonSerializer.Serialize(res, Wire).Should().NotContainAny("truncated", "omitted", "hint", "distilled");
	}

	[Fact]
	public async Task SessionList_Large_PrefixCut_MarkersAndHint()
	{
		// Rows are tiny (sessionId/agent/version) — blow the budget via long session ids.
		const int total = 8;
		var pad = new string('s', 8000);
		for (var i = 0; i < total; i++)
			await _sessions.UpsertAsync(Proj, $"{i:d2}-{pad}", "claude-code", [new SessionMessageInput("session", "x")]);

		var res = await SessionTools.SearchAsync(Http(), Flags(), _sessions, null!, Proj);

		res.Items.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		res.Truncated.Should().BeTrue();
		res.Omitted.Should().Be(total - res.Items.Count);
		res.Hint.Should().ContainAll("q", "session_get");
	}

	// ---- comments_list ----

	[Fact]
	public async Task CommentsList_Small_NoMarkers()
	{
		var node = Guid.NewGuid().ToString("N");
		await CommentTools.CreateAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", node, "alice", "short body");

		var res = await CommentTools.ListAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", node);

		res.Comments.Should().ContainSingle();
		res.Truncated.Should().BeNull();
		JsonSerializer.Serialize(res, Wire).Should().NotContainAny("truncated", "omitted", "hint");
	}

	[Fact]
	public async Task CommentsList_Large_PrefixCut_ChronologicalHeadKept()
	{
		var node = Guid.NewGuid().ToString("N");
		const int total = 20;
		var body = new string('c', 2500); // ~50k chars of bodies > the 30k budget
		var firstId = (await CommentTools.CreateAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", node, "alice", body)).Id!;
		for (var i = 1; i < total; i++)
			await CommentTools.CreateAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", node, "alice", body);

		var res = await CommentTools.ListAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", node);

		res.Comments.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		res.Comments[0].Id.Should().Be(firstId); // chronological head kept (prefix cut)
		res.Truncated.Should().BeTrue();
		res.Omitted.Should().Be(total - res.Comments.Count);
		res.Hint.Should().NotBeNull();
	}
}
