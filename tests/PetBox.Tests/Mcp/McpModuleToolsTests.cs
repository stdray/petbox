using System.Security.Claims;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Sessions.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Services;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// Exercises the tasks.*/memory.*/session.* tool methods directly (mocked
// HttpContext + real stores). The MCP transport itself is covered by the
// existing McpDataToolsTests; here we validate tool logic, auth guards, and
// the temporal integration.
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
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });

		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_memFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_sessFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);

		_boards = new TaskBoardStore(_db, _tasksFactory);
		_relations = new RelationStore(_tasksFactory);
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
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Tasks_Create_Upsert_Get_Roundtrip()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "roadmap");

		var nodes = McpInputs.Nodes(new object[]
		{
			new { key = "phase-16", status = "InProgress", body = "Data", priority = 100 },
			new { key = "wave-1", partOf = "phase-16", status = "Done", body = "Foundation", priority = 200 },
		});
		var up = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "roadmap", nodes);
		up.Applied.Should().BeTrue();
		up.Inserted.Should().Be(2);

		var get = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "roadmap", includeClosed: true);
		var keys = get.Nodes.Select(n => n.Key).ToList();
		keys.Should().Equal("phase-16", "wave-1"); // priority order
	}

	[Fact]
	public async Task Tasks_StaleUpsert_ReturnsConflict()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "b");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.Nodes(new[] { new { key = "n", status = "Todo", body = "v1" } }));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.Nodes(new[] { new { key = "n", status = "Done", body = "byB", version = 1 } }));
		var r = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.Nodes(new[] { new { key = "n", status = "Done", body = "byA", version = 1 } }));

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle();
		r.Conflicts[0].Kind.Should().Be("Stale");
	}

	[Fact]
	public async Task Tasks_Rename_ShowsLineage()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "b");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.Nodes(new[] { new { key = "old", status = "Done", body = "x" } }));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "b",
			McpInputs.Nodes(new[] { new { key = "new", status = "Done", body = "x", version = 1, prevKey = "old" } }));

		var get = await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "b", includeClosed: true);
		var node = get.Nodes.Single();
		node.Key.Should().Be("new");
		node.RenamedFrom.Should().Equal("old");
	}

	[Fact]
	public async Task Tasks_MissingScope_Throws()
	{
		// Tools throw on a failed assert; McpErrorEnvelopeFilter renders {error} on the wire
		// (covered by the transport tests). Direct unit calls observe the typed throw.
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
		(await TasksTools.SearchAsync(star, Flags(), _tasks, Proj, board: "x"))
			.Kind.Should().Be("simple");

		// ...while a key scoped to a different project is rejected for this one (throws;
		// the filter renders it as {error} on the wire).
		var other = Http("tasks:read,tasks:write", project: "other");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardListAsync(other, Flags(), _tasks, Proj));
	}

	[Fact]
	public async Task Memory_Upsert_Search_Roundtrip()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.StoreCreateAsync(http, Flags(), _db, _memory, Proj, "notes");
		await MemoryTools.UpsertAsync(http, Flags(), _db, _memory, Proj, "notes",
			McpInputs.Entries(new[]
			{
				new { key = "go", type = "reference", description = "Go style", body = "tabs not spaces", tags = new[] { "go", "style" } },
			}));

		// memory_search is THE read verb (list = search without q; replaced list+recall).
		var hits = await MemoryTools.SearchAsync(http, Flags(), _db, _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
			"tabs", scope: "project", store: "notes");
		hits.Items.Should().ContainSingle();
	}

	[Fact]
	public async Task Session_Upsert_Get_List()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "s1", "claude-code", "# plan");

		var got = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s1"))!;
		got.Content.Should().Be("# plan");

		// list = session_search without q (the former session.list); rows carry version.
		var list = await SessionTools.SearchAsync(http, Flags(), _sessionSvc, null!, Proj);
		list.Items.Should().ContainSingle();
		list.Items[0].Version.Should().Be(1);
		list.Items[0].Hits.Should().BeNull(); // no query — no episodic arm
	}

	// spec bounded-result-sets: session_get reads the blob incrementally — `length` is always
	// reported; `tail` returns the last N chars; `offset`+`limit` a window.
	[Fact]
	public async Task Session_Get_ReadsIncrementally()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "s2", "claude-code", "0123456789");

		// default: full blob + total length.
		var full = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2"))!;
		full.Content.Should().Be("0123456789");
		full.Length.Should().Be(10);

		// tail: last N chars.
		(await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2", tail: 4))!
			.Content.Should().Be("6789");

		// offset + limit: a window, clamped.
		(await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2", offset: 3, limit: 4))!
			.Content.Should().Be("3456");
	}

	// A missing id is a not-found ERROR, never a null result: session_get declares an
	// outputSchema, so a null (no structured content) is rejected by strict MCP clients as
	// -32600. The throw rides the isError channel via McpErrorEnvelopeFilter — which strict
	// clients accept (bug mcp-nullable-get-strict-32600). InvalidOperationException matches the
	// surface-wide not-found convention.
	[Fact]
	public async Task Session_Get_MissingId_Throws()
	{
		var http = Http("tasks:read,tasks:write");
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "does-not-exist"));
	}

	// session_append: the incremental writer against the server-authoritative cursor.
	// The gap reject is a STRUCTURED result (applied:false + reason:"gap" + lastOrdinal),
	// not an opaque throw — the client parses lastOrdinal and resends the tail.
	[Fact]
	public async Task Session_Append_Contiguous_Overlap_Gap()
	{
		var http = Http("tasks:read,tasks:write");

		static PetBox.Web.Mcp.Contract.SessionMessageDto[] Batch(params (string Role, string Content)[] m) =>
			m.Select(x => new PetBox.Web.Mcp.Contract.SessionMessageDto { Role = x.Role, Content = x.Content }).ToArray();

		// New session: cursor 0 → fromOrdinal 1.
		var first = await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sa", "claude-code", 1, Batch(("user", "q"), ("assistant", "a")));
		first.Applied.Should().BeTrue();
		first.LastOrdinal.Should().Be(2);
		first.Reason.Should().BeNull();

		// Overlapping re-send + tail: idempotent, no duplicates.
		var overlap = await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sa", "claude-code", 1, Batch(("user", "q"), ("assistant", "a"), ("user", "q2")));
		overlap.Applied.Should().BeTrue();
		overlap.LastOrdinal.Should().Be(3);
		overlap.Appended.Should().Be(1);

		// Gap: structured reject with the server cursor inside.
		var gap = await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sa", "claude-code", 9, Batch(("user", "late")));
		gap.Applied.Should().BeFalse();
		gap.Reason.Should().Be("gap");
		gap.LastOrdinal.Should().Be(3);

		// session_get sees the assembled dialogue.
		var got = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sa"))!;
		got.Version.Should().Be(3);
		got.Content.Should().Contain("q2");
	}

	[Fact]
	public async Task Session_Append_MissingWriteScope_Throws()
	{
		var http = Http("tasks:read"); // no tasks:write
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sa", "claude-code", 1,
				new[] { new PetBox.Web.Mcp.Contract.SessionMessageDto { Role = "user", Content = "x" } }));
	}

	// session_search against a foreign project returns an explicit, structured Unauthorized
	// (the filter renders the throw as {error} on the wire). The project guard fires before
	// the search service is touched, so a null service is never dereferenced.
	[Fact]
	public async Task Session_Search_CrossProjectKey_Throws()
	{
		var other = Http("tasks:read,memory:read", project: "other");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			SessionTools.SearchAsync(other, Flags(), null!, null!, Proj, "q"));
	}

	[Fact]
	public async Task Comments_Create_Reply_List_DeleteWithChildrenRejected()
	{
		var http = Http("tasks:read,tasks:write");
		// A 32-hex value is a NodeId and passes through unresolved (a non-hex value would be
		// treated as a slug on the board and required to resolve — uniform-node-refs).
		var node1 = Guid.NewGuid().ToString("N");
		var add = await CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
			[new CommentItemInput { NodeId = node1, Author = "alice", Body = "root body", Tags = new[] { "artifact:plan" } }]);
		add.Applied.Should().BeTrue();
		var id = add.Added.Single().Id;

		await CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
			[new CommentItemInput { NodeId = node1, Author = "bob", Body = "a reply", ParentId = id }]);

		var list = await CommentTools.SearchAsync(http, Flags(), _commentSvc, _tasks, Proj, board: "ideas", nodeId: node1);
		var rows = list.Items.ToList();
		rows.Should().HaveCount(2);
		rows.Single(c => c.Id == id).Tags.Should().Equal("artifact:plan");

		// Deleting a parent with an active reply throws (the filter renders it as {error}).
		await Assert.ThrowsAnyAsync<Exception>(() =>
			CommentTools.DeleteAsync(http, Flags(), _commentSvc, Proj, "ideas", id));
	}

	[Fact]
	public async Task Comments_MissingWriteScope_Throws()
	{
		var http = Http("tasks:read"); // no tasks:write
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
				[new CommentItemInput { NodeId = "n", Author = "a", Body = "b" }]));
	}

	[Fact]
	public async Task Idea_ReviewGate_RequiresSpecPlan_ThenAcceptable()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "ideas", "ideas");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "exploring", body = "x" } }));

		var node = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "ideas")).Nodes.Single();
		var nodeId = node.NodeId;
		var v = node.Version;

		// exploring -> review WITHOUT a spec_plan artifact: rejected by the gate (throws;
		// the filter renders it as {error} on the wire).
		var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "review", version = v } }), 0));
		blocked.Message.Should().Contain("spec_plan");

		// Add the spec_plan artifact, then the same transition applies.
		await CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
			[new CommentItemInput { NodeId = nodeId, Author = "claude", Body = "the plan", Tags = new[] { "artifact:spec_plan" } }]);
		var rev = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "review", version = v } }));
		rev.Applied.Should().BeTrue();

		// review -> accepted (the maintainer gate; enforceApproval is off so it applies).
		var v2 = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "ideas"))
			.Nodes.Single().Version;
		var acc = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "accepted", version = v2 } }));
		acc.Applied.Should().BeTrue();
	}

	[Fact]
	public async Task Idea_ExploringToAccepted_NoLongerAllowed_MustGoThroughReview()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "ideas", "ideas");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-y", type = "idea", status = "exploring", body = "x" } }));
		var v = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "ideas"))
			.Nodes.Single().Version;
		// The direct exploring->accepted transition was removed; you must pass through review.
		await Assert.ThrowsAsync<ArgumentException>(() => TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-y", type = "idea", status = "accepted", version = v } }), 0));
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
}
