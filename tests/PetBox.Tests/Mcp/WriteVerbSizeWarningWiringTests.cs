using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Sessions.Data;
using PetBox.Sessions.Services;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Mcp;

// card size-warning-not-wired-to-write-verbs: ModuleMcp.SizeWarningOrNull was wired to
// memory_remember / memory_upsert's success payload but NOT to the other four write verbs that
// carry a body — tasks_upsert, comments_upsert, session_append, session_upsert. Each carries the
// SAME SizeGuidanceText in its [Description] (WriteVerbSizeGuidanceTests), but the runtime
// `warning` field on an actually-warned APPLIED write was simply absent. One test per verb,
// mirroring MemoryToolsContractTests.Upsert_LargeRequestBody_AppliedWrite_ReturnsSizeWarning.
//
// Updated by work escape-inflation-warning: SizeWarningOrNull no longer compares an absolute
// Content-Length threshold — it compares the request body's WIRE size against what that same body
// would cost as raw UTF-8. In production McpWireBodyMeasurementMiddleware measures both while the
// body streams past; these tests call the tool bodies directly (bypassing the MCP pipeline), so
// the Http() helper below runs the SAME scanner over a real envelope. These stay WIRING tests —
// "does this verb surface the warning at all" — and the units they divide are pinned over a real
// POST /mcp in McpEscapeInflationRealPathTests.
public sealed class WriteVerbSizeWarningWiringTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _tasksFactory;
	readonly ScopedDbFactory<SessionsDb> _sessFactory;
	readonly TasksService _tasks;
	readonly CommentService _comments;
	readonly SessionService _sessions;

	public WriteVerbSizeWarningWiringTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-size-warning-wiring-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });

		_tasksFactory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_sessFactory = new ScopedDbFactory<SessionsDb>(Path.Combine(_dir, "sessions"), Scope.Project,
			c => new SessionsDb(SessionsDb.CreateOptions(c)), SessionsSchema.Ensure);

		_comments = new CommentService(_tasksFactory);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _tasksFactory), new RelationStore(_tasksFactory),
			new TagStore(_tasksFactory), _comments);
		_sessions = new SessionService(new SessionStore(_sessFactory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_tasksFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_sessFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// `wireBody`, when given, is measured by the production scanner exactly as the middleware would
	// have measured it off POST /mcp — these tests call the tool bodies directly, so nothing
	// publishes it for real. Omitted → SizeWarningOrNull sees "no measurement" and stays silent,
	// same as a call that never went through the MCP endpoint.
	static IHttpContextAccessor Http(string? wireBody = null)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		if (wireBody is not null)
			McpWireBody.Publish(ctx, wireBody);
		return new HttpContextAccessor { HttpContext = ctx };
	}

	// The escaped spelling of one fixed request, plus the multiplier the server must report for it
	// — derived from the two spellings' byte counts, never stated.
	static string EscapedBody => McpWireBody.Envelope(McpWireBody.CyrillicPayload, escaped: true);
	static string ExpectedMultiplier => McpWireBody.InflationOf(McpWireBody.CyrillicPayload).ToString("0.0");

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build());

	[Fact]
	public async Task TasksUpsert_EscapedRequestBody_AppliedWrite_ReturnsSizeWarning()
	{
		var http = Http(EscapedBody);
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "board");
		var nodes = McpInputs.Nodes(new object[] { new { key = "n1", body = "hi" } });

		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "board", nodes);

		res.Applied.Should().BeTrue();
		res.Warning.Should().Contain(ExpectedMultiplier);
	}

	[Fact]
	public async Task CommentsUpsert_EscapedRequestBody_AppliedWrite_ReturnsSizeWarning()
	{
		var http = Http(EscapedBody);
		var node = Guid.NewGuid().ToString("N"); // 32-hex passes through node-ref resolution unresolved
		var items = new[] { new CommentItemInput { Node = node, Author = "alice", Body = "a comment" } };

		var res = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "board", items);

		res.Applied.Should().BeTrue();
		res.Warning.Should().Contain(ExpectedMultiplier);
	}

	[Fact]
	public async Task SessionAppend_EscapedRequestBody_AppliedWrite_ReturnsSizeWarning()
	{
		var http = Http(EscapedBody);
		var messages = new[] { new SessionMessageDto { Role = "user", Content = "hi" } };

		var res = await SessionTools.AppendAsync(http, Flags(), _sessions, Proj, "s1", "claude-code", fromOrdinal: 1, messages);

		res.Applied.Should().BeTrue();
		res.Warning.Should().Contain(ExpectedMultiplier);
	}

	[Fact]
	public async Task SessionUpsert_EscapedRequestBody_ReturnsSizeWarning()
	{
		var http = Http(EscapedBody);

		var res = await SessionTools.UpsertAsync(http, Flags(), _sessions, Proj, "s1", "claude-code", "hello");

		res.Warning.Should().Contain(ExpectedMultiplier);
	}

	// The headline behavioral flip (card escape-inflation-warning): the SAME request, spelled raw
	// UTF-8, gets NO warning — the escaped spelling above is the only difference between them.
	[Fact]
	public async Task TasksUpsert_RawUtf8Body_NoSizeWarning()
	{
		var http = Http(McpWireBody.Envelope(McpWireBody.CyrillicPayload, escaped: false));
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "board");
		var nodes = McpInputs.Nodes(new object[] { new { key = "n1", body = "hi" } });

		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "board", nodes);

		res.Warning.Should().BeNull();
	}
}
