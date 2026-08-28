using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// Exercises the tasks_*/memory_*/session_* tool methods directly (mocked
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
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_memFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_sessFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), TestSchema.Sessions);

		_boards = new TaskBoardStore(_db.Factory(), _tasksFactory);
		_relations = new RelationStore(_tasksFactory);
		_tasks = new TasksService(_boards, _relations, new TagStore(_tasksFactory), new CommentService(_tasksFactory));
		_stores = new MemoryStore(_db.Factory(), _memFactory);
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

		var get = await TasksTools.SearchAsync(http, Flags(), _tasks, PetBox.Tests.Tasks.NoopTaskUsage.Recorder, PetBox.Tests.Tasks.NoopTaskUsage.Reader, Proj, board: "roadmap", statusKind: TestFacets.All);
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

		var get = await TasksTools.SearchAsync(http, Flags(), _tasks, PetBox.Tests.Tasks.NoopTaskUsage.Recorder, PetBox.Tests.Tasks.NoopTaskUsage.Reader, Proj, board: "b", statusKind: TestFacets.All);
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
		(await TasksTools.SearchAsync(star, Flags(), _tasks, PetBox.Tests.Tasks.NoopTaskUsage.Recorder, PetBox.Tests.Tasks.NoopTaskUsage.Reader, Proj, board: "x"))
			.Kind.Should().Be("simple");

		// ...while a key scoped to a different project is rejected for this one. Since the declaration
		// wave (work `authz-default-deny-delivery`, step 5) that rejection is made by
		// McpTenantEnforcementFilter ahead of the tool, not by an AssertProject inside it, so this
		// asserts on the PEP's verdict — the thing that actually decides — and the wildcard half above
		// keeps proving the same gate lets a legitimate caller through.
		await McpTenantPep.RefusesAsync(TestProjectCatalog.Instance, "tasks_board_list", Proj, claim: "other");
	}

	[Fact]
	public async Task Memory_Upsert_Search_Roundtrip()
	{
		var http = Http("memory:read,memory:write");
		await MemoryTools.StoreCreateAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes");
		await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notes",
			McpInputs.Entries(new[]
			{
				new { key = "go", type = "reference", description = "Go style", body = "tabs not spaces", tags = new[] { "go", "style" } },
			}));

		// memory_search is THE read verb (list = search without q; replaced list+recall).
		var hits = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new PetBox.Tests.Memory.NoopUsageRecorder(),
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
		got.LastOrdinal.Should().Be(1); // the ordinal cursor is always reported, not only when polling

		// list = session_search without q (the former session.list); rows carry version.
		var list = await SessionTools.SearchAsync(http, Flags(), _sessionSvc, null!, new PetBox.Tests.Memory.NoopUsageRecorder(), Proj);
		list.Items.Should().ContainSingle();
		list.Items[0].Version.Should().Be(1);
		list.Items[0].Hits.Should().BeNull(); // no query — no episodic arm
	}

	// spec bodylen-uniform-contract (card bodylen-contract-has-two-holes, hole 2): session_get
	// used to read the blob incrementally via tail/offset/limit — a private vocabulary outside
	// the family-wide bodyLen knob. Those three are GONE; session_get now follows the same
	// pointed-read contract as memory_get/tasks_node_get: omitted = FULL, 0 = no body, N>0 =
	// the first N chars, -1 = full. `length` (total chars) is always reported regardless.
	[Fact]
	public async Task Session_Get_BodyLen_OmittedIsFull()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "s2", "claude-code", "0123456789");

		var full = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2"))!;
		full.Content.Should().Be("0123456789");
		full.Length.Should().Be(10);
	}

	[Fact]
	public async Task Session_Get_BodyLen_ZeroIsNoBody()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "s2", "claude-code", "0123456789");

		var none = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2", bodyLen: 0))!;
		none.Content.Should().BeEmpty();
		none.Length.Should().Be(10); // length always reports the FULL blob, unaffected by bodyLen
	}

	[Fact]
	public async Task Session_Get_BodyLen_NCutsWithEllipsis()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "s2", "claude-code", "0123456789");

		var cut = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2", bodyLen: 4))!;
		cut.Content.Should().Be("0123…");
		cut.Length.Should().Be(10);
	}

	[Fact]
	public async Task Session_Get_BodyLen_MinusOneIsFull()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "s2", "claude-code", "0123456789");

		var full = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "s2", bodyLen: -1))!;
		full.Content.Should().Be("0123456789");
	}

	// ── card session-get-from-ordinal: the incremental read, on the ORDINAL axis ──────────
	// Batch 3 removed tail/offset/limit (they duplicated the LENGTH axis bodyLen now owns) and
	// took incremental reading of a growing session with them. It comes back as navigation by
	// MESSAGE ordinal — the unit session_append already writes against and session_search hits
	// already carry, and the one that cannot go stale (messages are immutable and dense 1..N).

	// Appends 3 dialogue messages to a fresh session and returns the tool's http context.
	static PetBox.Web.Mcp.Contract.SessionMessageDto[] Msgs(params (string Role, string Content)[] m) =>
		m.Select(x => new PetBox.Web.Mcp.Contract.SessionMessageDto { Role = x.Role, Content = x.Content }).ToArray();

	// The load-bearing invariant: a window is EXACTLY a suffix of the full body. If it were not,
	// "read from ordinal N" would mean something different from "the tail of what session_get
	// returns", and the two reads could not be stitched together by a polling client.
	[Fact]
	public async Task Session_Get_FromOrdinal_WindowIsSuffixOfFullBody()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sw", "claude-code", 1,
			Msgs(("user", "one"), ("assistant", "two"), ("user", "three")));

		var full = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sw"))!;

		for (long from = 1; from <= 4; from++)
		{
			var win = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sw", fromOrdinal: from))!;
			full.Content.Should().EndWith(win.Content, $"the window from {from} must be a suffix of the full body");
		}

		var fromTwo = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sw", fromOrdinal: 2))!;
		fromTwo.Content.Should().NotContain("one");
		fromTwo.Content.Should().Contain("two").And.Contain("three");
	}

	// The suffix invariant's sharp edge: a snapshot of ONE message renders verbatim (no `###`
	// header), so a naive slice-then-render would drop the header off a one-message TAIL and
	// stop being a suffix. The header mode follows the FULL message count, not the slice's.
	[Fact]
	public async Task Session_Get_FromOrdinal_LastMessageTail_KeepsRoleHeader()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "st", "claude-code", 1,
			Msgs(("user", "one"), ("assistant", "two")));

		var tail = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "st", fromOrdinal: 2))!;
		tail.Content.Should().Be("### assistant\n\ntwo");
	}

	// Past the end is the NORMAL poll for growth, not an error: empty body + the live cursor.
	[Fact]
	public async Task Session_Get_FromOrdinal_PastLast_IsEmptyBodyPlusCursor()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sp", "claude-code", 1,
			Msgs(("user", "a"), ("assistant", "b")));

		var beyond = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sp", fromOrdinal: 99))!;
		beyond.Content.Should().BeEmpty();
		beyond.LastOrdinal.Should().Be(2);
	}

	// The whole point, end to end: append 3 → poll from 4 (nothing new) → append 2 → poll from 4
	// returns EXACTLY the two new messages, never re-reading the three already held.
	[Fact]
	public async Task Session_Get_FromOrdinal_PollingCycle()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sc", "claude-code", 1,
			Msgs(("user", "m1"), ("assistant", "m2"), ("user", "m3")));

		var idle = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sc", fromOrdinal: 4))!;
		idle.Content.Should().BeEmpty();
		idle.LastOrdinal.Should().Be(3);

		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sc", "claude-code", 4,
			Msgs(("assistant", "m4"), ("user", "m5")));

		var grown = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sc", fromOrdinal: 4))!;
		grown.LastOrdinal.Should().Be(5);
		grown.Content.Should().Be("### assistant\n\nm4\n\n### user\n\nm5");
		grown.Content.Should().NotContain("m3");
	}

	// bodyLen × fromOrdinal compose as "from here, this many chars" — NEITHER takes precedence.
	// That precedence is exactly what made the old `tail` wrong ("takes precedence over
	// offset/limit"), so it is pinned here rather than left to the description.
	[Fact]
	public async Task Session_Get_FromOrdinal_ComposesWithBodyLen_NoPrecedence()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.AppendAsync(http, Flags(), _sessionSvc, Proj, "sm", "claude-code", 1,
			Msgs(("user", "one"), ("assistant", "two")));

		var window = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sm", fromOrdinal: 2))!.Content;
		var fullLen = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sm"))!.Length;

		// N>0 cuts the WINDOW, not the transcript: the first N chars of the window + "…".
		var cut = (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sm", bodyLen: 5, fromOrdinal: 2))!;
		cut.Content.Should().Be(string.Concat(window.AsSpan(0, 5), "…"));

		// 0 = no body, -1 = the full window — the same meanings as without fromOrdinal.
		(await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sm", bodyLen: 0, fromOrdinal: 2))!
			.Content.Should().BeEmpty();
		(await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sm", bodyLen: -1, fromOrdinal: 2))!
			.Content.Should().Be(window);

		// `length` is the CHAR axis and stays the FULL transcript's length under both knobs.
		foreach (var r in new[] { cut, (await SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sm", bodyLen: 0, fromOrdinal: 2))! })
			r.Length.Should().Be(fullLen);
	}

	// 1-based everywhere on this surface (session_append rejects <1 the same way); 0 is a
	// 0-based mental model that must fail loudly instead of silently re-reading the transcript.
	[Fact]
	public async Task Session_Get_FromOrdinal_BelowOne_Throws()
	{
		var http = Http("tasks:read,tasks:write");
		await SessionTools.UpsertAsync(http, Flags(), _sessionSvc, Proj, "sz", "claude-code", "x");
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => SessionTools.GetAsync(http, Flags(), _sessionSvc, Proj, "sz", fromOrdinal: 0));
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
			SessionTools.SearchAsync(other, Flags(), null!, null!, null!, Proj, "q"));
	}

	[Fact]
	public async Task Comments_Create_Reply_List_DeleteWithChildrenRejected()
	{
		var http = Http("tasks:read,tasks:write");
		// A 32-hex value is a NodeId and passes through unresolved (a non-hex value would be
		// treated as a slug on the board and required to resolve — uniform-node-refs).
		var node1 = Guid.NewGuid().ToString("N");
		var add = await CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
			[new CommentItemInput { Node = node1, Author = "alice", Body = "root body", Tags = new[] { "artifact:plan" } }]);
		add.Applied.Should().BeTrue();
		var id = add.Added.Single().Id;

		await CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
			[new CommentItemInput { Node = node1, Author = "bob", Body = "a reply", ParentId = id }]);

		var list = await CommentTools.SearchAsync(http, Flags(), _commentSvc, _tasks, Proj, board: "ideas", node: node1);
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
				[new CommentItemInput { Node = "n", Author = "a", Body = "b" }]));
	}

	[Fact]
	public async Task Idea_ReviewGate_RequiresSpecPlan_ThenAcceptable()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "ideas", "ideas");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "exploring", body = "x" } }));

		var node = (await TasksTools.SearchAsync(http, Flags(), _tasks, PetBox.Tests.Tasks.NoopTaskUsage.Recorder, PetBox.Tests.Tasks.NoopTaskUsage.Reader, Proj, board: "ideas")).Nodes.Single();
		var nodeId = node.NodeId;
		var v = node.Version;

		// exploring -> review WITHOUT a spec_plan artifact: rejected by the gate (throws;
		// the filter renders it as {error} on the wire).
		var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "review", version = v } }), 0));
		blocked.Message.Should().Contain("spec_plan");

		// Add the spec_plan artifact, then the same transition applies.
		await CommentTools.UpsertAsync(http, Flags(), _commentSvc, _tasks, Proj, "ideas",
			[new CommentItemInput { Node = nodeId, Author = "claude", Body = "the plan", Tags = new[] { "artifact:spec_plan" } }]);
		var rev = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-x", type = "idea", status = "review", version = v } }));
		rev.Applied.Should().BeTrue();

		// review -> accepted (the maintainer gate; enforceApproval is off so it applies).
		var v2 = (await TasksTools.SearchAsync(http, Flags(), _tasks, PetBox.Tests.Tasks.NoopTaskUsage.Recorder, PetBox.Tests.Tasks.NoopTaskUsage.Reader, Proj, board: "ideas"))
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
		var v = (await TasksTools.SearchAsync(http, Flags(), _tasks, PetBox.Tests.Tasks.NoopTaskUsage.Recorder, PetBox.Tests.Tasks.NoopTaskUsage.Reader, Proj, board: "ideas"))
			.Nodes.Single().Version;
		// The direct exploring->accepted transition was removed; you must pass through review.
		await Assert.ThrowsAsync<ArgumentException>(() => TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas",
			McpInputs.Nodes(new[] { new { key = "idea-y", type = "idea", status = "accepted", version = v } }), 0));
	}

	// spec methodology-write-scope — changing the rules that govern EXISTING nodes needs
	// methodology:write, a scope SEPARATE from tasks:write. tasks:write alone writes nodes
	// UNDER the rules; it must not be able to rewrite the rules themselves.
	const string TasksOnly = "tasks:read,tasks:write";
	const string TasksAndMethodology = "tasks:read,tasks:write,methodology:write";

	// Smallest definition the validator accepts (a methodology needs >=1 kind).
	static MethodologyDefInput MinimalDef() => new()
	{
		Name = "d",
		Kinds =
		[
			new MethodologyKindInput
			{
				Kind = "simple",
				Workflows =
				[
					new MethodologyWorkflowInput
					{
						Types = ["task"],
						Statuses = [new MethodologyStatusInput { Slug = "todo" }],
					},
				],
			},
		],
	};

	[Fact]
	public async Task Methodology_RulesUpsert_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.MethodologyRulesUpsertAsync(http, Flags(), _tasks, Proj, "inst",
				MinimalDef()));
	}

	[Fact]
	public async Task Methodology_Create_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "inst", "builtin", "simple"));
	}

	[Fact]
	public async Task Methodology_BoardAdopt_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardAdoptAsync(http, Flags(), _tasks, Proj, "b", "inst"));
	}

	// The gate is a scope check, not a ban: methodology:write actually opens the door.
	[Fact]
	public async Task Methodology_Create_WithMethodologyWrite_Succeeds()
	{
		var http = Http(TasksAndMethodology);
		var ack = await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "inst", "builtin", "simple");
		ack.Key.Should().Be("inst");
		ack.Boards.Should().NotBeEmpty();
	}

	// Owner decision (intake/finding-methodology-close-blast-radius): the criterion is
	// "a governance act over an EXISTING process", not only "changes the rules". These four
	// change no rules document and still retire/destroy/rewire a live process.
	[Fact]
	public async Task Methodology_Close_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.MethodologyCloseAsync(http, Flags(), _tasks, Proj, "inst"));
	}

	[Fact]
	public async Task BoardClose_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardCloseAsync(http, Flags(), _tasks, Proj, "b"));
	}

	[Fact]
	public async Task BoardReopen_WithoutMethodologyWrite_Throws()
	{
		// The inverse of a gated act: an ungated reopen would undo a governance freeze.
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardReopenAsync(http, Flags(), _tasks, Proj, "b"));
	}

	[Fact]
	public async Task BoardDelete_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardDeleteAsync(http, Flags(), _tasks, Proj, "b"));
	}

	[Fact]
	public async Task BoardSetWire_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.BoardSetWireAsync(http, Flags(), _tasks, Proj, "b", "s"));
	}

	// set_active moves the pointer tasks_methodology_guide resolves through. Board membership
	// still wins, so no node's enforcement changes — but the guide is the only control that
	// exists for CONVENTION gates, so moving it changes what every agent is taught the process
	// is. Gated under the owner-widened criterion.
	[Fact]
	public async Task MethodologySetActive_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.MethodologySetActiveAsync(http, Flags(), _tasks, Proj, "inst"));
	}

	// Clearing the pointer is the same governance act as setting it.
	[Fact]
	public async Task MethodologySetActive_Clear_WithoutMethodologyWrite_Throws()
	{
		var http = Http(TasksOnly);
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.MethodologySetActiveAsync(http, Flags(), _tasks, Proj, null));
	}

	// The second line I am holding: methodology_set_description writes a LIVE instance's rules
	// document through the same service call as the gated rules_upsert, and still needs no
	// governance scope — prose cannot change a rule. Structure is untouchable from here, and
	// the guide derives every invariant from structure. Gating it would rot the docs.
	[Fact]
	public async Task Methodology_SetDescription_NeedsNoMethodologyWrite()
	{
		var admin = Http(TasksAndMethodology);
		await TasksTools.MethodologyCreateAsync(admin, Flags(), _tasks, Proj, "d-inst", "builtin", "simple");

		var http = Http(TasksOnly);
		var ack = await TasksTools.MethodologySetDescriptionAsync(
			http, Flags(), _tasks, Proj, "d-inst", "kind", "prose set by a plain tasks:write key", kind: "simple");
		ack.Primitive.Should().Be("kind");
	}

	// The claim the ungated decision rests on, asserted rather than trusted: a set_description
	// call changes ONLY prose. If someone ever teaches this verb to touch structure, this test
	// goes red and the ungated decision must be revisited.
	[Fact]
	public async Task Methodology_SetDescription_CannotChangeStructure()
	{
		var admin = Http(TasksAndMethodology);
		await TasksTools.MethodologyCreateAsync(admin, Flags(), _tasks, Proj, "s-inst", "builtin", "simple");
		var before = await TasksTools.MethodologyRulesGetAsync(admin, Flags(), _tasks, Proj, "s-inst");

		await TasksTools.MethodologySetDescriptionAsync(
			admin, Flags(), _tasks, Proj, "s-inst", "kind", "a description", kind: "simple");

		var after = await TasksTools.MethodologyRulesGetAsync(admin, Flags(), _tasks, Proj, "s-inst");
		after.Kinds!.Select(k => k.Kind).Should().Equal(before.Kinds!.Select(k => k.Kind));
		after.Kinds!.SelectMany(k => k.Workflows!).SelectMany(w => w.Statuses!).Select(s => s.Slug)
			.Should().Equal(before.Kinds!.SelectMany(k => k.Workflows!).SelectMany(w => w.Statuses!).Select(s => s.Slug));
		after.Kinds!.SelectMany(k => k.Workflows!).SelectMany(w => w.Transitions!).Select(t => $"{t.From}->{t.To}")
			.Should().Equal(before.Kinds!.SelectMany(k => k.Workflows!).SelectMany(w => w.Transitions!).Select(t => $"{t.From}->{t.To}"));
	}

	// The line I am holding: board_create is NOT governance. It is constrained BY the rules
	// (kind must be declared, process-role singleton enforced) and alters nothing that already
	// exists — it adds an empty board. Gating it would put the routine verb agents use daily
	// behind the governance scope and buy nothing: you cannot change a process by adding a
	// board the rules already permit.
	[Fact]
	public async Task BoardCreate_NeedsNoMethodologyWrite()
	{
		var http = Http(TasksOnly);
		var meta = await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "plain");
		meta.Name.Should().Be("plain");
	}

	// The gate must not over-reach: an INERT template touches no live node, so the
	// criterion ("changes the rules for EXISTING nodes") does not bind and tasks:write
	// stays sufficient. A gate here would break template authoring for no security gain.
	[Fact]
	public async Task Methodology_TemplateUpsert_NeedsNoMethodologyWrite()
	{
		var http = Http(TasksOnly);
		var ack = await TasksTools.MethodologyTemplateUpsertAsync(http, Flags(), _tasks, Proj, "tmpl",
			MinimalDef());
		ack.Key.Should().Be("tmpl");
	}

	// methodology:write is a capability layered ON tasks:write (like tasks:approve), not a
	// replacement for it: it must not become a back door for a key that cannot write tasks.
	[Fact]
	public async Task Methodology_Create_MethodologyWriteWithoutTasksWrite_StillThrows()
	{
		var http = Http("tasks:read,methodology:write");
		await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
			TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "inst", "builtin", "simple"));
	}

	[Fact]
	public void MethodologyWrite_IsInTheScopeCatalog()
	{
		// The catalog is the single source of truth: the create-key UI renders from it and
		// the server validates submitted scope strings against it. A scope enforced but not
		// catalogued cannot be granted — the gate would be unopenable.
		var (valid, invalid) = ApiKeyScopes.Validate("methodology:write");
		valid.Should().Equal(ApiKeyScopes.MethodologyWrite);
		invalid.Should().BeEmpty();
	}

	static IHttpContextAccessor Http(string scopes, string? project = null)
	{
		var id = new ClaimsIdentity([new Claim("project", project ?? Proj), new Claim("scopes", scopes)], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
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
