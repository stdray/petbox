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

// card size-warning-not-wired-to-write-verbs: ModuleMcp.SizeWarningOrNull (measuring
// Request.ContentLength against WriteCallSizeGuidanceBytes) was wired to memory_remember /
// memory_upsert's success payload but NOT to the other four write verbs that carry a body —
// tasks_upsert, comments_upsert, session_append, session_upsert. Each carries the SAME
// SizeGuidanceText in its [Description] (WriteVerbSizeGuidanceTests), but the runtime `warning`
// field on an actually-oversized APPLIED write was simply absent. One test per verb, mirroring
// MemoryToolsContractTests.Upsert_LargeRequestBody_AppliedWrite_ReturnsSizeWarning: an applied
// write whose request ContentLength exceeds the WriteCallSizeGuidanceBytes guidance threshold
// (12,000 bytes as of work drop-size-number-from-tool-descriptions, raised from 8,000) must
// carry a `warning` naming both the accepted size and the threshold.
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

	static IHttpContextAccessor Http(long contentLength)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.ContentLength = contentLength;
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
			["Features:Memory"] = "true",
		}).Build());

	[Fact]
	public async Task TasksUpsert_LargeRequestBody_AppliedWrite_ReturnsSizeWarning()
	{
		var http = Http(15_000);
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "board");
		var nodes = McpInputs.Nodes(new object[] { new { key = "n1", body = "hi" } });

		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "board", nodes);

		res.Applied.Should().BeTrue();
		res.Warning.Should().Contain("15,000").And.Contain("12,000");
	}

	[Fact]
	public async Task CommentsUpsert_LargeRequestBody_AppliedWrite_ReturnsSizeWarning()
	{
		var http = Http(15_000);
		var node = Guid.NewGuid().ToString("N"); // 32-hex passes through node-ref resolution unresolved
		var items = new[] { new CommentItemInput { Node = node, Author = "alice", Body = "a comment" } };

		var res = await CommentTools.UpsertAsync(http, Flags(), _comments, _tasks, Proj, "board", items);

		res.Applied.Should().BeTrue();
		res.Warning.Should().Contain("15,000").And.Contain("12,000");
	}

	[Fact]
	public async Task SessionAppend_LargeRequestBody_AppliedWrite_ReturnsSizeWarning()
	{
		var http = Http(15_000);
		var messages = new[] { new SessionMessageDto { Role = "user", Content = "hi" } };

		var res = await SessionTools.AppendAsync(http, Flags(), _sessions, Proj, "s1", "claude-code", fromOrdinal: 1, messages);

		res.Applied.Should().BeTrue();
		res.Warning.Should().Contain("15,000").And.Contain("12,000");
	}

	[Fact]
	public async Task SessionUpsert_LargeRequestBody_ReturnsSizeWarning()
	{
		var http = Http(15_000);

		var res = await SessionTools.UpsertAsync(http, Flags(), _sessions, Proj, "s1", "claude-code", "hello");

		res.Warning.Should().Contain("15,000").And.Contain("12,000");
	}

	[Fact]
	public async Task TasksUpsert_SmallRequestBody_NoSizeWarning()
	{
		var http = Http(200);
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "board");
		var nodes = McpInputs.Nodes(new object[] { new { key = "n1", body = "hi" } });

		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "board", nodes);

		res.Warning.Should().BeNull();
	}
}
