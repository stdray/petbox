using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Contract;
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
// shared ResponseBudget helper): memory_search / session_search / comments_search are prefix-cut
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
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_memFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_sessFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);

		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _tasksFactory), new RelationStore(_tasksFactory),
			new TagStore(_tasksFactory), new CommentService(_tasksFactory));
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _memFactory));
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
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
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

		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
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
		var res = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
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

		var snipped = await MemoryTools.SearchAsync(Http(), Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			scope: "project", store: "notes", bodyLen: 20, limit: 0);

		snipped.Items.Count.Should().Be(total);
		snipped.Truncated.Should().BeNull();
	}

	// ---- session_search (listing mode — the former session.list) ----

	[Fact]
	public async Task SessionList_Small_NoMarkers()
	{
		await _sessions.UpsertAsync(Proj, "s1", "claude-code", [new SessionMessageInput("session", "x")]);

		var res = await SessionTools.SearchAsync(Http(), Flags(), _sessions, null!, new PetBox.Tests.Memory.NoopUsageRecorder(), Proj);

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

		var res = await SessionTools.SearchAsync(Http(), Flags(), _sessions, null!, new PetBox.Tests.Memory.NoopUsageRecorder(), Proj);

		res.Items.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		res.Truncated.Should().BeTrue();
		res.Omitted.Should().Be(total - res.Items.Count);
		res.Hint.Should().ContainAll("q", "session_get", "nextCursor");
		res.NextCursor.Should().NotBeNullOrEmpty();
	}

	// card session-search-listing-cursor: the listing is KEYSET-paged like tasks_search's.
	async Task SeedSessions(int n, string prefix = "s")
	{
		for (var i = 0; i < n; i++)
			await _sessions.UpsertAsync(Proj, $"{prefix}{i:d2}", "claude-code", [new SessionMessageInput("session", "x")]);
	}

	Task<PetBox.Web.Mcp.Contract.SessionSearchResultView> ListPage(int limit = 0, string? cursor = null) =>
		SessionTools.SearchAsync(Http(), Flags(), _sessions, null!, new PetBox.Tests.Memory.NoopUsageRecorder(), Proj,
			limit: limit, cursor: cursor);

	[Fact]
	public async Task SessionList_Limit_PagesWholeListing_NoDupesNoGaps()
	{
		await SeedSessions(7);
		var seen = new List<string>();
		string? cursor = null;
		var pages = 0;
		do
		{
			var page = await ListPage(limit: 3, cursor: cursor);
			page.Items.Count.Should().BeLessThanOrEqualTo(3);
			seen.AddRange(page.Items.Select(i => i.SessionId));
			cursor = page.NextCursor;
			pages++;
		} while (cursor is not null && pages < 10);

		pages.Should().Be(3);
		seen.Should().Equal(Enumerable.Range(0, 7).Select(i => $"s{i:d2}"), "sessionId order, every row exactly once");
		(await ListPage(limit: 7)).NextCursor.Should().BeNull("a limit that covers everything is the end");
		(await ListPage()).NextCursor.Should().BeNull("unbounded by default");
	}

	[Fact]
	public async Task SessionList_InsertBetweenPages_NeitherDuplicatesNorDrops()
	{
		await SeedSessions(6);
		var first = await ListPage(limit: 3);
		await _sessions.UpsertAsync(Proj, "s00-new", "claude-code", [new SessionMessageInput("session", "x")]); // sorts BEFORE the boundary
		await _sessions.UpsertAsync(Proj, "s99-new", "claude-code", [new SessionMessageInput("session", "x")]); // sorts after

		var second = await ListPage(limit: 10, cursor: first.NextCursor);

		second.Items.Select(i => i.SessionId).Should().Equal("s03", "s04", "s05", "s99-new");
	}

	[Fact]
	public async Task SessionList_CursorContinues_WhenLimitChanges()
	{
		await SeedSessions(6);
		var first = await ListPage(limit: 2);

		var second = await ListPage(limit: 3, cursor: first.NextCursor);

		second.Items.Select(i => i.SessionId).Should().Equal("s02", "s03", "s04");
		second.NextCursor.Should().NotBeNull();
	}

	[Fact]
	public async Task SessionList_CursorFromAnotherProject_IsRefused_NotRestarted()
	{
		await SeedSessions(3);
		var foreign = new KeysetCursor(KeysetCursor.FingerprintOf("session_search:list", "other-project"), "", "s00", "other-project").Encode();

		var act = () => ListPage(limit: 2, cursor: foreign);

		await act.Should().ThrowAsync<ArgumentException>().WithMessage("*DIFFERENT query*");
	}

	[Fact]
	public async Task SessionList_BudgetCut_StillIssuesACursor_AndTheWalkLosesNothing()
	{
		const int total = 8;
		var pad = new string('s', 8000);
		for (var i = 0; i < total; i++)
			await _sessions.UpsertAsync(Proj, $"{i:d2}-{pad}", "claude-code", [new SessionMessageInput("session", "x")]);

		var seen = new List<string>();
		string? cursor = null;
		var pages = 0;
		do
		{
			var page = await ListPage(cursor: cursor);
			if (pages == 0)
			{
				page.Truncated.Should().BeTrue();
				page.Hint.Should().Contain("nextCursor");
			}
			seen.AddRange(page.Items.Select(i => i.SessionId));
			cursor = page.NextCursor;
			pages++;
		} while (cursor is not null && pages < 20);

		pages.Should().BeGreaterThan(1);
		seen.Should().HaveCount(total).And.OnlyHaveUniqueItems();
	}

	// ---- comments_search (listing mode — the former comments_list) ----

	static PetBox.Web.Mcp.Contract.CommentItemInput NewComment(string node, string body) =>
		new() { Node = node, Author = "alice", Body = body };

	[Fact]
	public async Task CommentsList_Small_NoMarkers()
	{
		var node = Guid.NewGuid().ToString("N");
		await CommentTools.UpsertAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", [NewComment(node, "short body")]);

		var res = await CommentTools.SearchAsync(Http(), Flags(), _comments, _tasks, Proj, board: "ideas", node: node);

		res.Items.Should().ContainSingle();
		res.Truncated.Should().BeNull();
		JsonSerializer.Serialize(res, Wire).Should().NotContainAny("truncated", "omitted", "hint");
	}

	[Fact]
	public async Task CommentsList_Large_PrefixCut_ChronologicalHeadKept()
	{
		// bodyLen:-1 forced explicitly — the listing default is now the same ~240-char snippet
		// as with q (card comments-search-full-body-in-listing), so a 20x2500-char thread no
		// longer overflows the 30k budget on its own; this test is about the prefix-cut/budget
		// mechanics, so it opts into full bodies to keep exercising that path.
		var node = Guid.NewGuid().ToString("N");
		const int total = 20;
		var body = new string('c', 2500); // ~50k chars of bodies > the 30k budget
		var firstId = (await CommentTools.UpsertAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", [NewComment(node, body)])).Added[0].Id;
		for (var i = 1; i < total; i++)
			await CommentTools.UpsertAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", [NewComment(node, body)]);

		var res = await CommentTools.SearchAsync(Http(), Flags(), _comments, _tasks, Proj, board: "ideas", node: node, bodyLen: -1);

		res.Items.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		res.Items[0].Id.Should().Be(firstId); // chronological head kept (prefix cut)
		res.Truncated.Should().BeTrue();
		res.Omitted.Should().Be(total - res.Items.Count);
		res.Hint.Should().NotBeNull();
	}

	[Fact]
	public async Task CommentsList_BudgetCut_StillIssuesACursor_AndTheWalkLosesNothing()
	{
		var node = Guid.NewGuid().ToString("N");
		var body = new string('c', 2500);
		var ids = new List<string>();
		for (var i = 0; i < 20; i++)
			ids.Add((await CommentTools.UpsertAsync(Http(), Flags(), _comments, _tasks, Proj, "ideas", [NewComment(node, body)])).Added[0].Id);

		var seen = new List<string>();
		string? cursor = null;
		var pages = 0;
		do
		{
			var page = await CommentTools.SearchAsync(Http(), Flags(), _comments, _tasks, Proj, board: "ideas", node: node, bodyLen: -1, cursor: cursor);
			if (pages == 0)
			{
				page.Truncated.Should().BeTrue();
				page.Hint.Should().Contain("nextCursor");
			}
			seen.AddRange(page.Items.Select(i => i.Id));
			cursor = page.NextCursor;
			pages++;
		} while (cursor is not null && pages < 20);

		pages.Should().BeGreaterThan(1);
		seen.Should().Equal(ids);
	}
}
