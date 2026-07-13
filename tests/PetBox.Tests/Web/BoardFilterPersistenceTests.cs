using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Config;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.ProjectHome;
using PetBox.Web.Rendering;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// board-view-cross-device / board-filters-server-state, end to end through TaskBoardModel with a
// REAL DB-backed ISettingsResolver (not NullSettingsResolver — TaskBoardViewModeTests' fixture
// proves resolution-order logic with no DB; this file proves the DB/cookie round trip itself,
// which is the actual FOUC fix: the SAME preference must already be resolved before the FIRST
// render, on a DIFFERENT TaskBoardModel instance/HttpContext — simulating a second page load, or a
// second device — with no query string and no cookie carried over from the write).
public sealed class BoardFilterPersistenceTests : IDisposable
{
	const string Proj = "proj";
	const string UserId = "42";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;
	readonly SettingsResolver _settings;

	public BoardFilterPersistenceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-boardfilters-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
		_settings = new SettingsResolver(new SettingsStore(_db.Factory()), new NoSecrets());
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	// A fresh TaskBoardModel + a fresh HttpContext each call — simulating a NEW page load (or a
	// different device: same authenticated user, no cookie/query carried over from a previous
	// call) rather than reusing state across "requests". userId:null simulates anonymous.
	TaskBoardModel Model(string board, string? userId = UserId, string? uiCookie = null)
	{
		var http = new DefaultHttpContext();
		if (userId is not null)
			http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PetBoxClaims.UserId, userId)], "test"));
		if (uiCookie is not null)
			http.Request.Headers.Append("Cookie", $"{UiStateResolver.CookieName}={Uri.EscapeDataString(uiCookie)}");

		var model = new TaskBoardModel(Flags(), _tasks, new CommentService(_factory), _settings)
		{
			WorkspaceKey = "ws",
			ProjectKey = Proj,
			Board = board,
			PageContext = new PageContext { HttpContext = http },
		};
		return model;
	}

	async Task AddNode(string board, string key, string title, long priority, string status, string? partOf = null) =>
		await _tasks.UpsertAsync(Proj, board, [new NodePatch { Key = key, Title = title, Priority = priority, Status = status, PartOf = partOf, Body = "" }]);

	// ── board-view-cross-device: explicit ?view= persists, and a LATER load (no query at all,
	// simulating a different device) already resolves it — no redirect anywhere in this path. ──

	[Fact]
	public async Task ExplicitViewMode_PersistsToDb_AndResolvesOnALaterLoad_NoQueryString()
	{
		await _store.CreateAsync(Proj, "b1", null, "simple");

		var first = Model("b1");
		first.ViewMode = BoardViewModeNames.Kanban;
		await first.OnGetAsync(default);
		first.ResolvedViewMode.Should().Be(BoardViewModeNames.Kanban);

		// A SECOND, independent instance/HttpContext — no query string, no cookie: only the DB
		// row written above can make this resolve to kanban.
		var second = Model("b1");
		await second.OnGetAsync(default);
		second.ResolvedViewMode.Should().Be(BoardViewModeNames.Kanban,
			"the saved per-(project,board) DB preference must apply on a later load with no explicit override — the whole point of board-view-cross-device");
		second.ContentPartialName.Should().Be("_BoardViewKanban");
	}

	[Fact]
	public async Task ExplicitViewModeWithTagBy_PersistsBothTogether()
	{
		await _store.CreateAsync(Proj, "b2", null, "simple");
		await AddNode("b2", "n1", "N1", 50, "Todo");

		var first = Model("b2");
		first.ViewMode = BoardViewModeNames.Tags;
		first.By = "area";
		await first.OnGetAsync(default);

		var second = Model("b2");
		await second.OnGetAsync(default);
		second.ResolvedViewMode.Should().Be(BoardViewModeNames.Tags);
	}

	[Fact]
	public async Task ExplicitFields_PersistsToDb_AndResolvesOnALaterLoad()
	{
		await _store.CreateAsync(Proj, "b3", null, "simple");

		var first = Model("b3");
		first.FieldsSetParam = "1";
		first.FieldsParam = [BoardFieldNames.Type, BoardFieldNames.Priority];
		await first.OnGetAsync(default);
		first.Fields.Type.Should().BeTrue();
		first.Fields.Priority.Should().BeTrue();
		first.Fields.Tags.Should().BeFalse();

		var second = Model("b3");
		await second.OnGetAsync(default);
		second.Fields.Type.Should().BeTrue();
		second.Fields.Priority.Should().BeTrue();
		second.Fields.Tags.Should().BeFalse();
	}

	[Fact]
	public async Task DifferentBoards_HaveIndependentSavedViewModes()
	{
		await _store.CreateAsync(Proj, "boarda", null, "simple");
		await _store.CreateAsync(Proj, "boardb", null, "simple");

		var a = Model("boarda");
		a.ViewMode = BoardViewModeNames.Kanban;
		await a.OnGetAsync(default);

		// boardb never got an explicit choice — must NOT inherit boarda's saved kanban pick.
		var b = Model("boardb");
		await b.OnGetAsync(default);
		b.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
	}

	[Fact]
	public async Task DifferentUser_DoesNotSeeAnotherUsersSavedViewMode()
	{
		await _store.CreateAsync(Proj, "b4", null, "simple");

		var user1 = Model("b4", userId: "1");
		user1.ViewMode = BoardViewModeNames.Kanban;
		await user1.OnGetAsync(default);

		var user2 = Model("b4", userId: "2");
		await user2.OnGetAsync(default);
		user2.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree,
			"a per-user DB preference must never leak to a different authenticated user");
	}

	[Fact]
	public async Task Anonymous_NoUserId_NeverThrows_FallsBackToMethodologyDefault()
	{
		await _store.CreateAsync(Proj, "b5", null, "simple");

		var m = Model("b5", userId: null);
		m.ViewMode = BoardViewModeNames.Kanban; // explicit — still applies THIS render
		var result = await m.OnGetAsync(default);

		result.Should().NotBeNull();
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Kanban);
	}

	// ── board-filters-server-state: active-only / sort are GLOBAL DB [Setting]s ──

	[Fact]
	public async Task ActiveOnlyAndSort_ResolveFromDb_BoardIndependent()
	{
		await _store.CreateAsync(Proj, "b6", null, "simple");
		await _store.CreateAsync(Proj, "b7", null, "simple");

		var old = await _settings.GetAsync<BoardPreferences>(Scope.User, UserId);
		var updated = old with { ActiveOnly = false, SortBy = BoardSortKeys.Title, SortDesc = true };
		await _settings.SetAsync(Scope.User, UserId, updated, old, 42);

		// Confirmed still correct (per the work node's own "verify" ask): the SAME global
		// preference applies to every board this user opens, not just the one it was set from.
		foreach (var board in new[] { "b6", "b7" })
		{
			var m = Model(board);
			await m.OnGetAsync(default);
			m.ActiveOnly.Should().BeFalse();
			m.SortBy.Should().Be(BoardSortKeys.Title);
			m.SortDesc.Should().BeTrue();
		}
	}

	[Fact]
	public async Task UnknownSavedSortBy_FallsBackToPriority_NeverThrows()
	{
		await _store.CreateAsync(Proj, "b8", null, "simple");

		// A stale value referencing a removed sort key — written directly, bypassing the
		// endpoint's own validation, to prove the RESOLUTION side tolerates it too.
		var old = await _settings.GetAsync<BoardPreferences>(Scope.User, UserId);
		await _settings.SetAsync(Scope.User, UserId, old with { SortBy = "bogus" }, old, 42);

		var m = Model("b8");
		var result = await m.OnGetAsync(default);
		result.Should().NotBeNull();
		m.SortBy.Should().Be(BoardSortKeys.Priority);
	}

	[Fact]
	public async Task SortComparer_OrdersRootSiblingsByTheResolvedKey()
	{
		await _store.CreateAsync(Proj, "b9", null, "simple");
		await AddNode("b9", "alpha", "Alpha", priority: 50, status: "Todo");
		await AddNode("b9", "beta", "Beta", priority: 10, status: "Todo");

		// Default (priority asc): beta(10) before alpha(50) — unchanged from the old hardcoded order.
		var byPriority = Model("b9");
		await byPriority.OnGetAsync(default);
		byPriority.Nodes.Select(n => n.Key).Should().Equal("beta", "alpha");

		// Switch the GLOBAL sort to title desc.
		var old = await _settings.GetAsync<BoardPreferences>(Scope.User, UserId);
		await _settings.SetAsync(Scope.User, UserId, old with { SortBy = BoardSortKeys.Title, SortDesc = true }, old, 42);

		// "Beta" > "Alpha" descending — sort now resolves from the DB [Setting], not a hardcoded
		// Priority-then-Key order.
		var byTitleDesc = Model("b9");
		await byTitleDesc.OnGetAsync(default);
		byTitleDesc.Nodes.Select(n => n.Key).Should().Equal("beta", "alpha");
	}

	// ── board-filters-server-state: collapsed-node set is a per-(project,board) COOKIE ──

	[Fact]
	public async Task CollapsedSet_ResolvesFromCookie_HidesDescendantsOfACollapsedAncestor()
	{
		await _store.CreateAsync(Proj, "b10", null, "simple");
		await AddNode("b10", "parent", "Parent", priority: 10, status: "Todo");
		await AddNode("b10", "child", "Child", priority: 10, status: "Todo", partOf: "parent");

		var m = Model("b10");
		await m.OnGetAsync(default);
		var parentId = m.Nodes.Single(n => n.Key == "parent").NodeId;
		var childNode = m.Nodes.Single(n => n.Key == "child");

		// No cookie yet: nothing collapsed.
		m.CollapsedNodeIds.Should().BeEmpty();
		m.IsHiddenByCollapse(childNode.ParentNodeId).Should().BeFalse();

		var cookie = "{\"collapsedByBoard\":{\"proj/b10\":[\"" + parentId + "\"]}}";
		var m2 = Model("b10", uiCookie: cookie);
		await m2.OnGetAsync(default);

		m2.CollapsedNodeIds.Should().Contain(parentId);
		var childNode2 = m2.Nodes.Single(n => n.Key == "child");
		m2.IsHiddenByCollapse(childNode2.ParentNodeId).Should().BeTrue(
			"the child's parent is in the collapsed set for THIS board, resolved before render — no localStorage/JS post-load hide involved");
	}

	[Fact]
	public async Task CollapsedSet_IsPerBoard_NotSharedWithAnotherBoard()
	{
		await _store.CreateAsync(Proj, "b11", null, "simple");
		await _store.CreateAsync(Proj, "b12", null, "simple");

		var cookie = """{"collapsedByBoard":{"proj/b11":["some-node-id"]}}""";
		var other = Model("b12", uiCookie: cookie);
		await other.OnGetAsync(default);

		other.CollapsedNodeIds.Should().BeEmpty(
			"a board's collapsed set must not leak into a DIFFERENT board sharing the same cookie");
	}
}
