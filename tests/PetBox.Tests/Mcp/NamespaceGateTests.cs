using System.Security.Claims;
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
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// The agent-MCP-layer namespace-creation GATE (spec agent-namespace-provisioning, variant C —
// hard opt-in). An unknown namespace named by an AGENT write verb is REJECTED, not auto-created;
// creation is explicit. The reserved system stores always auto-vivify, and the gate lives at the
// tool layer ONLY — the service door keeps auto-vivifying for background jobs.

// ---- pure unit tests (no DB): reserved-set drift, matcher, telemetry markup ----
public sealed class NamespaceGateUnitTests
{
	// The gate's reserved set must cover the UNION of every set that legitimately creates a store
	// below the tool layer. It is hardcoded in the Web layer (the module boundary forbids
	// referencing the store door there), so this pins it against the authoritative sources — a new
	// system store cannot silently drift out of the reserve.
	[Fact]
	public void ReservedStores_Are_The_Union_Of_The_Authoritative_Sets()
	{
		var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notes" };
		expected.UnionWith(MemoryStore.SystemStoreNames);   // session-digests, autocaptured, canon
		expected.UnionWith(MemoryStores.SensitiveNames);    // ops

		MemoryTools.ReservedStores.Should().BeEquivalentTo(expected);
		// The exact five, spelled out — a human-readable pin next to the derived one above.
		MemoryTools.ReservedStores.Should().BeEquivalentTo(
			new[] { "notes", "canon", "autocaptured", "session-digests", "ops" });
	}

	// Edit-distance ONLY — no prefix leg. A deliberate derivation like `notes-archive` must NOT be
	// flagged as a near-miss of `notes` (the old prefix leg did exactly that, nudging an agent to
	// collapse a distinct namespace back into the base name).
	[Fact]
	public void Suggest_IsEditDistanceOnly_DoesNotFlagDeliberateDerivations()
	{
		NamespaceSuggest.Nearest("notes-archive", ["notes", "journal"]).Should().BeEmpty();
		// A genuine typo (edit distance within budget) IS surfaced.
		NamespaceSuggest.Nearest("cannon", ["canon", "notes"]).Should().Equal("canon");
		// Nothing close → no suggestion (a suggestion that fires on everything is noise).
		NamespaceSuggest.Nearest("zzzzzzzz", ["notes", "canon"]).Should().BeEmpty();
	}

	// [LogArg] round-trip: `store`/`board` on the write + lifecycle verbs are registered so
	// namespace creation is observable in PetBox.Mcp.ToolCalls. The value carried is the closed
	// namespace identifier (Value mode), the same treatment memory_search's `store` already had.
	[Theory]
	[InlineData("memory_upsert", "store")]
	[InlineData("memory_remember", "store")]
	[InlineData("memory_store_create", "store")]
	[InlineData("memory_store_delete", "store")]
	[InlineData("tasks_upsert", "board")]
	[InlineData("tasks_board_create", "board")]
	public void Namespace_Param_Is_Marked_For_Telemetry(string tool, string param)
	{
		var marked = McpLoggedArgs.For(tool);
		var arg = marked.SingleOrDefault(a => a.Name == param);
		arg.Name.Should().Be(param, $"{tool}.{param} must carry [LogArg]");
		arg.Mode.Should().Be(LogArgMode.Value);
		arg.LogProperty.Should().Be($"Arg_{param}");
		arg.SpanTag.Should().Be($"petbox.arg.{param}");
	}
}

// ---- memory gate (DB-backed) ----
public sealed class MemoryNamespaceGateTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public MemoryNamespaceGateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memgate-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Upsert_UnknownStore_IsRejected_WithSuggestion()
	{
		var http = Http();
		var entries = McpInputs.Entries(new object[] { new { key = "k", type = "project", description = "d", body = "b" } });

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, "notez", entries));
		ex.Message.Should().Contain("does not exist").And.Contain("memory_store_create");
		ex.Message.Should().Contain("notes"); // did-you-mean picks the reserved near-miss

		// Nothing was written — the store did not come into being by being named.
		(await _store.ExistsAsync(Proj, "notez")).Should().BeFalse();
	}

	[Fact]
	public async Task Remember_UnknownStore_IsRejected_SuggestsExistingStore()
	{
		var http = Http();
		await _memory.CreateStoreAsync(Proj, "journal", null);   // an existing, non-reserved store

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "x", store: "journl"));
		ex.Message.Should().Contain("journal");
	}

	[Fact]
	public async Task ReservedStores_AutoVivify_OnFirstWrite()
	{
		var http = Http();
		foreach (var store in new[] { "notes", "canon", "autocaptured", "session-digests", "ops" })
		{
			var res = await MemoryTools.UpsertAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, Proj, store,
				McpInputs.Entries(new object[] { new { key = "k", type = "project", description = "d", body = "b" } }));
			res.Applied.Should().BeTrue($"reserved store '{store}' must auto-vivify");
			(await _store.ExistsAsync(Proj, store)).Should().BeTrue();
		}
	}

	// The gate is tool-layer ONLY: the service door still auto-vivifies (background jobs and
	// reserved plumbing must keep working). A DIRECT service upsert creates the store with no gate.
	[Fact]
	public async Task ServiceDoor_StillAutoVivifies_ArbitraryStore()
	{
		var input = new MemoryEntryInput { Key = "k", Version = 0, Type = "Project", Body = "b" };
		await _memory.UpsertAsync(Proj, "svc-only-store", [input], [], ct: default);
		(await _store.ExistsAsync(Proj, "svc-only-store")).Should().BeTrue();
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "memory:read,memory:write")], "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Memory"] = "true",
		}).Build());
}

// ---- tasks gate (DB-backed) ----
public sealed class TasksNamespaceGateTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public TasksNamespaceGateTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-tasksgate-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Upsert_UnknownBoard_IsRejected_WithSuggestion()
	{
		var http = Http();
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "work", null);   // an existing board

		var nodes = McpInputs.NodesJson("""[{"key":"a","status":"Todo","title":"A"}]""");
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "work2", nodes));
		ex.Message.Should().Contain("does not exist").And.Contain("tasks_board_create");
		ex.Message.Should().Contain("work"); // did-you-mean

		(await _tasks.BoardExistsAsync(Proj, "work2")).Should().BeFalse();
	}

	// The explicit-create path is unaffected: create the board, then the same upsert lands.
	[Fact]
	public async Task Upsert_AfterBoardCreate_Applies()
	{
		var http = Http();
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "plan", null);
		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "plan",
			McpInputs.NodesJson("""[{"key":"a","status":"Todo","title":"A"}]"""));
		res.Applied.Should().BeTrue();
	}

	// Methodology provisioning still lands its boards (created via CreateBoardAsync, not by typing
	// them through tasks_upsert), so the gate lets them through.
	[Fact]
	public async Task MethodologyProvisionedBoards_PassTheGate()
	{
		var http = Http();
		var created = await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");
		created.Boards.Should().NotBeEmpty();
		foreach (var board in created.Boards)
			(await _tasks.BoardExistsAsync(Proj, board.Name)).Should().BeTrue($"methodology board '{board.Name}' must exist for the gate");
	}

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build());
}
