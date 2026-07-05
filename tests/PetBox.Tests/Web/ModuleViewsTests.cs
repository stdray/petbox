using System.Net;
using LinqToDB.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Settings;

namespace PetBox.Tests.Web;

// Shared per-class host for ModuleViewsTests (xUnit news the test class per test, so
// without this fixture each of the ~19 tests boots its own WebApplicationFactory). No
// per-test reset is needed: the class only ADDS distinctly-named containers ("roadmap"
// and "notes" seeded once here; "ordertest"/"specnoise" created by single tests with
// exists-guards), and every assertion is Contains/NotContain on names no other test
// touches — accumulated state is invisible across tests.
public sealed class ModuleViewsFixture : IAsyncLifetime
{
	public const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public ModuleViewsFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-modviews-" + Guid.NewGuid().ToString("N"));
		var dbPath = Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db");
		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = $"Data Source={dbPath};Cache=Shared",
						["Features:Tasks"] = "true",
						["Features:Memory"] = "true",
						["Features:Data"] = "true",
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
				// Isolate Tasks/Memory/Sessions files to a per-test temp dir — otherwise the
				// test writes into the shared dev data dir and runs migrations there.
				b.ConfigureServices(svc =>
				{
					Replace<PetBox.Tasks.Data.TasksDb>(svc, "tasks", c => new PetBox.Tasks.Data.TasksDb(PetBox.Tasks.Data.TasksDb.CreateOptions(c)), PetBox.Tasks.Data.TasksSchema.Ensure);
					Replace<PetBox.Memory.Data.MemoryDb>(svc, "memory", c => new PetBox.Memory.Data.MemoryDb(PetBox.Memory.Data.MemoryDb.CreateOptions(c)), PetBox.Memory.Data.MemorySchema.Ensure);
					Replace<PetBox.Sessions.Data.SessionsDb>(svc, "sessions", c => new PetBox.Sessions.Data.SessionsDb(PetBox.Sessions.Data.SessionsDb.CreateOptions(c)), PetBox.Sessions.Data.SessionsSchema.Ensure);
				});
			});
	}

	void Replace<TDb>(IServiceCollection svc, string sub, Func<string, TDb> create, Action<string> ensure) where TDb : DataConnection
	{
		var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<TDb>));
		if (existing is not null) svc.Remove(existing);
		svc.AddSingleton<IScopedDbFactory<TDb>>(_ => new ScopedDbFactory<TDb>(
			Path.Combine(_baseDir, sub), Scope.Project, create, ensure));
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
		if (!await boards.ExistsAsync("$system", "roadmap"))
			await boards.CreateAsync("$system", "roadmap", "the plan");
		var stores = scope.ServiceProvider.GetRequiredService<PetBox.Memory.Data.IMemoryStore>();
		if (!await stores.ExistsAsync("$system", "notes"))
			await stores.CreateAsync("$system", "notes", "agent notes");
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// Covers the main-UI read views for the Tasks / Memory / Sessions modules:
// the feature gate (module off → 404 on a board/store detail), the happy-path
// board/store listing for the seeded $system project, and unknown-container 404.
// Mirrors NavTreeAndDataViewTests (cookie auth + in-memory config).
// Out of the serialized WebAppFactory collection: the fixture writes only the constant
// ASPNETCORE_ENVIRONMENT=Testing (never nulled) and uses its own Guid temp db.
public sealed class ModuleViewsTests : IClassFixture<ModuleViewsFixture>
{
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	const string TestPassword = "test123";

	public ModuleViewsTests(ModuleViewsFixture fx)
	{
		_factory = fx.Factory;
		_client = fx.Client;
	}

	// Logs in (anti-forgery + cookie) and returns the authenticated response for url.
	async Task<HttpResponseMessage> GetAuthedAsync(string url)
	{
		var resp = await _client.GetAsync(url);
		if (resp.StatusCode != HttpStatusCode.Found) return resp;

		var loginPage = await _client.GetAsync("/Login");
		var loginHtml = await loginPage.Content.ReadAsStringAsync();
		var tokenStart = loginHtml.IndexOf("__RequestVerificationToken", StringComparison.Ordinal);
		var valueStart = loginHtml.IndexOf("value=\"", tokenStart, StringComparison.Ordinal) + 7;
		var valueEnd = loginHtml.IndexOf('"', valueStart);
		var token = loginHtml[valueStart..valueEnd];
		var cookies = loginPage.Headers.GetValues("Set-Cookie").ToList();

		var loginReq = new HttpRequestMessage(HttpMethod.Post, "/Login?returnUrl=" + Uri.EscapeDataString(url));
		loginReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["username"] = "admin",
			["password"] = TestPassword,
			["returnUrl"] = url,
			["__RequestVerificationToken"] = token,
		});
		foreach (var c in cookies) loginReq.Headers.Add("Cookie", c.Split(';')[0]);

		var loginResp = await _client.SendAsync(loginReq);
		var authCookie = loginResp.Headers.GetValues("Set-Cookie").First();
		var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Add("Cookie", authCookie.Split(';')[0]);
		return await _client.SendAsync(req);
	}

	[Fact]
	public async Task Tasks_ListsCreatedBoard()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-board-name=\"roadmap\"");
	}

	[Fact]
	public async Task TaskBoard_UnknownBoard_Returns404()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/tasks/does-not-exist");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task TaskBoard_OrdersByTree_NotFlatPriority_AndRendersThreeLevels()
	{
		const string board = "ordertest";
		using (var scope = _factory.Services.CreateScope())
		{
			var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
			var relations = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.IRelationStore>();
			if (!await boards.ExistsAsync("$system", board))
				await boards.CreateAsync("$system", board, "ordering");
			var ctx = boards.GetContext("$system");
			// Early root p1 (priority 10) whose child wlow has a deliberately huge priority,
			// and a later root p2 (priority 500) in between. A flat priority sort would emit
			// p1(10), p2(500), wlow(900) — the child drifting past p2 (finding D11). Nesting
			// is part_of edges now, not the key path.
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode { Board = board, Key = "p1", NodeId = "id-p1", Version = 0, Status = "Pending", Name = "Phase one", Body = "", Priority = 10 },
				new PetBox.Tasks.Data.PlanNode { Board = board, Key = "wlow", NodeId = "id-wlow", Version = 0, Status = "Pending", Name = "Low wave", Body = "", Priority = 900 },
				new PetBox.Tasks.Data.PlanNode { Board = board, Key = "deep", NodeId = "id-deep", Version = 0, Status = "Pending", Name = "Deep task", Body = "", Priority = 1 },
				new PetBox.Tasks.Data.PlanNode { Board = board, Key = "p2", NodeId = "id-p2", Version = 0, Status = "Pending", Name = "Phase two", Body = "", Priority = 500 },
			}, partition: n => n.Board == board);
			await relations.CreateAsync("$system", "part_of", "id-wlow", "id-p1"); // wlow under p1
			await relations.CreateAsync("$system", "part_of", "id-deep", "id-wlow"); // deep under wlow
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/tasks/{board}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		var wlow = html.IndexOf("data-node-key=\"wlow\"", StringComparison.Ordinal);
		var deep = html.IndexOf("data-node-key=\"deep\"", StringComparison.Ordinal);
		var p2 = html.IndexOf("data-node-key=\"p2\"", StringComparison.Ordinal);

		wlow.Should().BeGreaterThan(0);
		deep.Should().BeGreaterThan(0);
		p2.Should().BeGreaterThan(0);
		// DFS keeps the child (and grandchild) under p1, before p2 — not flat by priority.
		wlow.Should().BeLessThan(p2);
		deep.Should().BeLessThan(p2);
		// The third level (part_of depth 2, root = 0) renders, indented.
		html.Should().Contain("data-node-key=\"deep\" data-depth=\"2\"");
	}

	// On a spec board `defined` is the ~universal default status → pure visual noise; the card
	// suppresses that badge and shows one only for a non-default (terminal `deprecated`) state
	// (spec-board-status-noise #9). Nodes are seeded straight through TemporalStore to bypass the
	// idea/FSM gates — this exercises the render path, not the write path.
	[Fact]
	public async Task SpecBoard_SuppressesDefaultDefinedStatus_ShowsTerminalDeprecated()
	{
		const string board = "specnoise";
		using (var scope = _factory.Services.CreateScope())
		{
			var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
			if (!await boards.ExistsAsync("$system", board))
				await boards.CreateAsync("$system", board, "spec noise", "spec");
			var ctx = boards.GetContext("$system");
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode { Board = board, Key = "reqa", NodeId = "id-reqa", Version = 0, Status = "defined", Type = "spec", Name = "Req A", Body = "", Priority = 1 },
				new PetBox.Tasks.Data.PlanNode { Board = board, Key = "reqb", NodeId = "id-reqb", Version = 0, Status = "deprecated", Type = "spec", Name = "Req B", Body = "", Priority = 2 },
			}, partition: n => n.Board == board);
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/tasks/{board}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Both cards render (deprecated is closed but the server emits it; the client hides it).
		html.Should().Contain("data-node-key=\"reqa\"");
		html.Should().Contain("data-node-key=\"reqb\"");
		// The default `defined` gets NO status badge; the non-default `deprecated` still gets one.
		// The badge shows the declared human Name (`deprecated` → "Deprecated"); the slug is unchanged.
		html.Should().NotContain("data-testid=\"node-status\">defined");
		html.Should().Contain("data-testid=\"node-status\">Deprecated");
	}

	// ui-spec-status-board-node-mismatch: the node DETAIL page must apply the SAME spec-board status
	// suppression as the board (previously it always showed the badge, so board and node disagreed).
	// A non-terminal spec status (`defined`) → NO status badge; the terminal `deprecated` → badge
	// shows. A non-spec (work) board keeps its status badge — non-spec boards are unaffected. Seeded
	// through TemporalStore to bypass the idea/FSM gates — this exercises the render path.
	[Fact]
	public async Task SpecNodeDetail_HidesNonTerminalStatus_ShowsDeprecated_WorkNodeUnaffected()
	{
		const string spec = "specnodepage";
		const string work = "worknodepage";
		using (var scope = _factory.Services.CreateScope())
		{
			var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
			if (!await boards.ExistsAsync("$system", spec))
				await boards.CreateAsync("$system", spec, "spec node page", "spec");
			if (!await boards.ExistsAsync("$system", work))
				await boards.CreateAsync("$system", work, "work node page"); // default (simple) kind
			var ctx = boards.GetContext("$system");
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode { Board = spec, Key = "sdef", NodeId = "id-sdef", Version = 0, Status = "defined", Type = "spec", Name = "Spec defined", Body = "", Priority = 1 },
				new PetBox.Tasks.Data.PlanNode { Board = spec, Key = "sdep", NodeId = "id-sdep", Version = 0, Status = "deprecated", Type = "spec", Name = "Spec deprecated", Body = "", Priority = 2 },
			}, partition: n => n.Board == spec);
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode { Board = work, Key = "wtask", NodeId = "id-wtask", Version = 0, Status = "InProgress", Type = "task", Name = "Work task", Body = "", Priority = 1 },
			}, partition: n => n.Board == work);
		}

		// Spec node, non-terminal `defined`: NO status badge — matches the board's hide rule.
		using var defResp = await GetAuthedAsync($"/ui/$system/$system/tasks/{spec}/sdef");
		defResp.StatusCode.Should().Be(HttpStatusCode.OK);
		var defHtml = await defResp.Content.ReadAsStringAsync();
		defHtml.Should().Contain("data-testid=\"node-name\""); // the detail page did render
		defHtml.Should().NotContain("data-testid=\"node-status\""); // …but no status badge

		// Spec node, terminal `deprecated`: the status badge shows (as on the board), with the
		// declared human Name.
		using var depResp = await GetAuthedAsync($"/ui/$system/$system/tasks/{spec}/sdep");
		var depHtml = await depResp.Content.ReadAsStringAsync();
		depHtml.Should().Contain("data-testid=\"node-status\">Deprecated");

		// Work (non-spec) board node: the status badge always shows — behaviour unchanged. The
		// PascalCase slug `InProgress` renders as the human Name "In progress".
		using var workResp = await GetAuthedAsync($"/ui/$system/$system/tasks/{work}/wtask");
		var workHtml = await workResp.Content.ReadAsStringAsync();
		workHtml.Should().Contain("data-testid=\"node-status\">In progress");
	}

	// server-md-render / reader-view: a node body is markdown rendered to HTML on the SERVER inside
	// a semantic <article>, so the initial response carries real <article>/<p> (what Firefox's
	// isProbablyReaderable counts) — not raw markdown text hydrated later on the client.
	[Fact]
	public async Task NodeDetail_RendersBodyMarkdownServerSide_AsArticleWithParagraphs()
	{
		const string board = "readerview";
		using (var scope = _factory.Services.CreateScope())
		{
			var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
			if (!await boards.ExistsAsync("$system", board))
				await boards.CreateAsync("$system", board, "reader view");
			var ctx = boards.GetContext("$system");
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode
				{
					Board = board, Key = "rv", NodeId = "id-rv", Version = 0, Status = "Pending",
					Name = "Reader node",
					Body = "The first paragraph of body text.\n\nAnd a **second** paragraph here.",
					Priority = 1,
				},
			}, partition: n => n.Board == board);
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/tasks/{board}/rv");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Server-rendered markdown: real markup in the initial DOM, not raw markdown in [data-md].
		html.Should().Contain("<article");
		html.Should().Contain("<p>The first paragraph of body text.</p>");
		html.Should().Contain("<strong>second</strong>");
		html.Should().NotContain("data-md=");
	}

	// ui-comments-presentation: a comment on the node detail page renders its body through the
	// SAME shared _MdBody / IMarkdownRenderer as the node body (so `**bold**` becomes <strong>,
	// not literal asterisks), and carries a created timestamp — a `time.local-time` element whose
	// server text is the yyyy-MM-dd HH:mm convention used elsewhere, localized client-side.
	[Fact]
	public async Task NodeDetail_CommentBody_RendersMarkdown_AndCarriesTimestamp()
	{
		const string board = "commentmd";
		using (var scope = _factory.Services.CreateScope())
		{
			var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
			if (!await boards.ExistsAsync("$system", board))
				await boards.CreateAsync("$system", board, "comment md");
			var ctx = boards.GetContext("$system");
			await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
			{
				new PetBox.Tasks.Data.PlanNode
				{
					Board = board, Key = "cm", NodeId = "id-cm", Version = 0, Status = "Pending",
					Name = "Comment host", Body = "", Priority = 1,
				},
			}, partition: n => n.Board == board);

			var comments = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Contract.ICommentService>();
			var added = await comments.AddAsync("$system", board, "id-cm", null, "alice",
				"a **bold** remark", null);
			added.Applied.Should().BeTrue();
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/tasks/{board}/cm");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Body rendered as GFM via the shared renderer, not plain text.
		html.Should().Contain("data-testid=\"comment-body\"");
		html.Should().Contain("<strong>bold</strong>");
		// The raw markdown source appears exactly once — inside the (hidden until toggled)
		// comment-edit-body textarea (comments-ui-edit), never inside the rendered read body.
		System.Text.RegularExpressions.Regex.Count(html, System.Text.RegularExpressions.Regex.Escape("a **bold** remark")).Should().Be(1);
		html.Should().Contain("data-testid=\"comment-edit-body\"");
		// Created timestamp is present as a localizable `time.local-time` element.
		html.Should().MatchRegex(
			"<time class=\"local-time[^\"]*\"[^>]*data-testid=\"comment-time\"");
	}

	// Installs a methodology DEFINITION declaring the custom kind `support` (own statuses,
	// custom terminal `closed`, quick-add DISABLED) and creates a board of that kind with
	// one open + one closed node. Seeds through the store/TemporalStore like the other
	// board fixtures — this exercises the render path, not the write path.
	async Task SeedSupportKindBoardAsync(string board)
	{
		using var scope = _factory.Services.CreateScope();
		var tasks = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Contract.ITasksService>();
		// Shared per-class host: a definition may already exist from an earlier test in this
		// class — baseline on the current version instead of assuming a fresh project (0).
		var currentDef = await tasks.GetMethodologyDefinitionAsync("$system");
		await tasks.DefineMethodologyAsync("$system", new PetBox.Tasks.Workflow.MethodologyDefinition("custom",
		[
			new PetBox.Tasks.Workflow.MethodologyKindDef("support", QuickAddAllowed: false,
			[
				new PetBox.Tasks.Workflow.MethodologyWorkflowDef(["ticket"],
					[
						new("new", "New", PetBox.Tasks.Workflow.StatusKind.Open),
						new("closed", "Closed", PetBox.Tasks.Workflow.StatusKind.TerminalOk),
					],
					[new("new", "closed")]),
			]),
		]), version: currentDef?.Version ?? 0);

		var boards = scope.ServiceProvider.GetRequiredService<PetBox.Tasks.Data.ITaskBoardStore>();
		if (!await boards.ExistsAsync("$system", board))
			await boards.CreateAsync("$system", board, "support tickets", "support");
		var ctx = boards.GetContext("$system");
		await PetBox.Core.Data.Temporal.TemporalStore.UpsertAsync(ctx, new[]
		{
			new PetBox.Tasks.Data.PlanNode { Board = board, Key = "t1", NodeId = "id-t1", Version = 0, Status = "new", Type = "ticket", Name = "Open ticket", Body = "", Priority = 1 },
			new PetBox.Tasks.Data.PlanNode { Board = board, Key = "t2", NodeId = "id-t2", Version = 0, Status = "closed", Type = "ticket", Name = "Closed ticket", Body = "", Priority = 2 },
		}, partition: n => n.Board == board);
	}

	// A definition-declared CUSTOM kind must resolve through MethodologyRuntime, not the
	// preset fallback (which read any unknown slug as Simple): the kind badge shows the
	// custom slug, the custom statuses render with definition-classified badges, the custom
	// terminal `closed` is marked closed (data-closed drives the active-only hiding), and
	// quick-add follows the definition (disabled here — the Simple fallback would show it).
	[Fact]
	public async Task TaskBoard_CustomDefinedKind_ResolvesProcessFromDefinition_NotPresetFallback()
	{
		const string board = "tickets";
		await SeedSupportKindBoardAsync(board);

		using var resp = await GetAuthedAsync($"/ui/$system/$system/tasks/{board}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// Kind badge = the definition's slug, not the Simple fallback.
		html.Should().Contain("data-kind=\"support\"");
		html.Should().NotContain("data-kind=\"simple\"");
		// The custom statuses render, classified by the DEFINITION: open → info, terminal → success.
		// The badge shows each status's declared Name ("New"/"Closed"); the slugs stay new/closed.
		html.Should().Contain("badge-info badge-sm\" data-testid=\"node-status\">New");
		html.Should().Contain("badge-success badge-sm\" data-testid=\"node-status\">Closed");
		// The custom terminal is CLOSED (hidden under active-only); the open node is not.
		html.Should().MatchRegex("data-node-key=\"t2\"[^>]*data-closed=\"true\"");
		html.Should().MatchRegex("data-node-key=\"t1\"[^>]*data-closed=\"false\"");
		// Quick-add follows the definition (false); the Simple fallback would render the form.
		html.Should().NotContain("data-testid=\"task-create\"");
	}

	// The admin boards list resolves kind badges through the runtime too — a custom-kind
	// board shows its declared slug, not "simple".
	[Fact]
	public async Task TasksAdmin_CustomDefinedKind_BadgeShowsDefinitionKind()
	{
		await SeedSupportKindBoardAsync("tickets");

		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		html.Should().MatchRegex("""data-board-name="tickets"[\s\S]*?data-testid="board-kind"[^>]*>support<""");
	}

	[Fact]
	public async Task Memory_ListsCreatedStore()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/memory");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-store-name=\"notes\"");
	}

	[Fact]
	public async Task MemoryStore_UnknownStore_Returns404()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/memory/does-not-exist");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
	}

	// The reserved "$workspace" memory container (seeded by M028/M031) resolves as a project
	// key — the memory page must render for it despite the `$` in the route (routing + project
	// resolution). No stores in a fresh DB → the empty state, still 200.
	[Fact]
	public async Task WorkspaceMemory_PageRendersForDollarWorkspace()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$workspace/memory");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("memory-title");
		html.Should().Contain("memory-empty"); // no stores yet, but the page resolved the project
	}

	// The workspace dashboard surfaces a first-class "Workspace memory" entry linking to the
	// shared container, and keeps "$workspace" itself out of the project grid (it's a memory
	// container, not a user project).
	[Fact]
	public async Task Dashboard_ShowsWorkspaceMemoryEntry_AndHidesContainerFromGrid()
	{
		using var resp = await GetAuthedAsync("/ui/$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("dashboard-workspace-memory");
		html.Should().Contain("/ui/$system/$workspace/memory");
		html.Should().NotContain("data-project-key=\"$workspace\""); // container excluded from the grid
	}

	// A VALID member landing on the real workspace key still renders the dashboard (200) —
	// the junk-key rejection must not break the legitimate default-workspace landing.
	[Fact]
	public async Task Dashboard_KnownWorkspaceKey_Renders200()
	{
		using var resp = await GetAuthedAsync("/ui/$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"dashboard-title\"");
	}

	// The /ui/{workspaceKey} catch-all must NOT silently fall back to the resolved default
	// ($system) for an unknown/non-member workspace key — it returns 404 (ui-404-and-junk-route).
	[Fact]
	public async Task Dashboard_UnknownWorkspaceKey_Returns404_NotSilentSystem()
	{
		using var resp = await GetAuthedAsync("/ui/no-such-workspace");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		// The friendly custom 404 re-executes the minimal-layout Error page, not the $system status.
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"error-title\"");
		html.Should().NotContain("data-testid=\"dashboard-projects\"");
	}

	// An entirely unmatched path renders the custom 404 (StatusCodePages re-execute) in the
	// minimal Error shell — not the bare browser 404, and not the authenticated topbar/sidebar.
	[Fact]
	public async Task UnknownPath_Returns404_WithCustomErrorPage_InMinimalShell()
	{
		using var resp = await _client.GetAsync("/this/path/does/not/exist");
		resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"error-title\""); // custom error page rendered
		html.Should().Contain("404");
		// Minimal (_PublicLayout) shell — no authenticated chrome.
		html.Should().NotContain("data-testid=\"nav-ws-memory\"");
	}

	// The sidebar's workspace-level group: "Shared memory" is a live link to the shared
	// container (the "coming soon" placeholder is gone), "Shared config" sits in the same
	// top group, and the workspace-admin entry is gone (admin is reached via the header gear).
	[Fact]
	public async Task Sidebar_SharedMemoryLive_SharedConfigTop_NoWorkspaceAdmin()
	{
		using var resp = await GetAuthedAsync("/ui/$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().NotContain("coming soon");
		html.Should().Contain("data-testid=\"nav-ws-memory\"");
		html.Should().MatchRegex(
			"""<a href="/ui/\$system/\$workspace/memory"[^>]*data-testid="nav-ws-memory""");
		html.Should().Contain("Shared memory");
		html.Should().Contain("data-testid=\"nav-shared-config\"");
		html.Should().NotContain("data-testid=\"nav-ws-admin\"");
	}

	// The sidebar surfaces the CURRENT project's task boards as sub-nav under the "Tasks"
	// item (ui-sidebar-boards-nav): on a project-scoped page each board renders as a nested
	// link to its board page, so the boards are reachable without first opening the Tasks list.
	[Fact]
	public async Task Sidebar_CurrentProjectBoards_RenderUnderTasks_WithHrefs()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// The Tasks item became an expandable node with the board sub-link beneath it.
		html.Should().Contain("data-testid=\"nav-proj-tasks-node\"");
		html.Should().MatchRegex(
			"""<a href="/ui/\$system/\$system/tasks/roadmap"[^>]*data-testid="nav-proj-task-board""");
	}

	// The board the page is on is highlighted in the sidebar sub-nav (active class), including
	// on a node page beneath the board.
	[Fact]
	public async Task Sidebar_ActiveBoard_IsHighlighted()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/tasks/roadmap");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// The roadmap board sub-link carries the active class on its own board page.
		html.Should().MatchRegex(
			"""<a href="/ui/\$system/\$system/tasks/roadmap" class="active" data-testid="nav-proj-task-board""");
	}

	[Fact]
	public async Task Sessions_EmptyList_RendersOk()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/sessions");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("sessions-empty");
	}

	// ui-session-gfm-render: the session detail page renders its content as GFM markdown through the
	// ONE shared renderer (_MdBody / IMarkdownRenderer) — the multi-message `### role` headers become
	// real <h3> headings and code fences become <pre><code>, not a raw `### user …` blob in a <pre>.
	[Fact]
	public async Task SessionDetail_RendersContentAsGfmMarkdown_NotRawBlob()
	{
		const string sessionId = "gfm-session";
		using (var scope = _factory.Services.CreateScope())
		{
			var store = scope.ServiceProvider.GetRequiredService<PetBox.Sessions.Data.ISessionStore>();
			var messages = new[]
			{
				new PetBox.Sessions.Contract.SessionMessage(1, "user", "Run `dotnet build` and report."),
				new PetBox.Sessions.Contract.SessionMessage(2, "assistant", "Done:\n\n```\nBuild succeeded.\n```"),
			};
			await store.UpsertAsync("$system", new PetBox.Sessions.Data.SessionRow
			{
				SessionId = sessionId,
				Agent = "claude",
				ContentZ = PetBox.Sessions.Data.SessionContent.Encode(messages),
				Version = 2,
				Updated = DateTime.UtcNow,
			});
		}

		using var resp = await GetAuthedAsync($"/ui/$system/$system/sessions/{sessionId}");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// The `### role` headers are rendered to real heading markup by the shared renderer …
		html.Should().Contain("<h3");
		html.Should().MatchRegex("<h3[^>]*>user</h3>");
		html.Should().MatchRegex("<h3[^>]*>assistant</h3>");
		// … the code fence became a code block, and it went through the shared _MdBody surface …
		html.Should().Contain("<pre><code>");
		html.Should().Contain("data-testid=\"session-body\"");
		// … so the raw markdown header text is NOT emitted literally.
		html.Should().NotContain("### user");
	}

	[Fact]
	public async Task TasksAdmin_RendersCreateForm_AndListsBoard()
	{
		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/tasks");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("board-create-form");
		html.Should().Contain("data-board-name=\"roadmap\"");
	}

	[Fact]
	public async Task MemoryAdmin_RendersCreateForm_AndListsStore()
	{
		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/memory");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("store-create-form");
		html.Should().Contain("data-store-name=\"notes\"");
	}

	// A system store (IsSystem — e.g. session-digests) is badged and its Delete button removed on
	// the admin memory page, mirroring the ProjectLogs self-log guard (ui-admin-memory-system-store-guard).
	[Fact]
	public async Task MemoryAdmin_SystemStore_BadgedAndNotDeletable()
	{
		using (var scope = _factory.Services.CreateScope())
		{
			var stores = scope.ServiceProvider.GetRequiredService<PetBox.Memory.Data.IMemoryStore>();
			if (!await stores.ExistsAsync("$system", "session-digests"))
				await stores.CreateAsync("$system", "session-digests", "digests");
		}

		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/memory");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// The system store's card carries the badge and its protected note, not a Delete button.
		html.Should().MatchRegex(
			"""data-store-name="session-digests"[\s\S]*?data-testid="store-system-badge"[^>]*>system<""");
		html.Should().MatchRegex(
			"""data-store-name="session-digests"[\s\S]*?data-testid="store-system-note">""");
		// The ordinary "notes" store still gets a Delete button.
		html.Should().Contain("data-testid=\"store-delete\"");
	}

	[Fact]
	public async Task Connect_RendersMintForm()
	{
		using var resp = await GetAuthedAsync("/ui/admin/ws/$system/projects/$system/connect");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("connect-mint-form"); // GET shows the mint form (key not yet minted)
	}

	// The Config subpages are custom-routed (@page "/ui/{workspaceKey}/config/..."), so the
	// asp-page/asp-route tag helpers can't build their URLs and render an empty href="" — a dead
	// link. Sibling links must be built via Routes.* to yield real, clickable URLs.
	// Regression guard for ui-deadlinks-asp-page.
	[Fact]
	public async Task ConfigHistory_BackAndClearLinks_HaveRealHrefs()
	{
		using var resp = await GetAuthedAsync("/ui/$system/config/history");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().MatchRegex("href=\"/ui/\\$system/config\"[\\s\\S]{0,80}?data-testid=\"back-to-config\""); // ← All bindings
		html.Should().Contain("href=\"/ui/$system/config/history\""); // Clear
	}

	[Fact]
	public async Task ConfigPreview_BackLink_HasRealHref()
	{
		using var resp = await GetAuthedAsync("/ui/$system/config/preview");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().MatchRegex("href=\"/ui/\\$system/config\"[\\s\\S]{0,80}?data-testid=\"back-to-config\"");
	}

	[Fact]
	public async Task ConfigEditor_BackAndCancelLinks_HaveRealHref()
	{
		using var resp = await GetAuthedAsync("/ui/$system/config/editor");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("href=\"/ui/$system/config\""); // both ← All bindings and Cancel
		html.Should().MatchRegex("href=\"/ui/\\$system/config\"[^>]*data-testid=\"config-cancel-btn\"");
	}

	// The trace page is custom-routed too and its "← traces" link was missing the workspace
	// route value (WorkspaceKey unbound), so asp-page produced an empty href. The link now
	// builds via Routes.ProjectTraces. Empty log store → trace-not-found, but the page (and its
	// back link) still render.
	[Fact]
	public async Task Trace_BackToTracesLink_HasRealHref()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/traces/nonexistent-trace");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("href=\"/ui/$system/$system/traces\""); // ← traces
		html.Should().Contain("&larr; traces");
	}

	[Fact]
	public async Task Doc_Index_IsPublic_NoRedirect()
	{
		// Anonymous client (no cookie) — doc pages must NOT redirect to /Login.
		using var resp = await _client.GetAsync("/doc");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("doc-index-title");
	}

	[Fact]
	public async Task Doc_Agent_IsPublic_ShowsMcpUrlAndNodeModel()
	{
		using var resp = await _client.GetAsync("/doc/agent");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("doc-agent");
		html.Should().Contain("/mcp");
		html.Should().Contain("flat slug"); // node model documented (flat key + partOf)
		html.Should().Contain("tasks_search"); // the unified read verb documented
		html.Should().NotContain("{{mcp}}"); // the mcp-endpoint placeholder was substituted at render, not leaked
	}

	[Fact]
	public async Task Doc_Methodology_IsPublic_ShowsRails()
	{
		using var resp = await _client.GetAsync("/doc/methodology");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("doc-methodology");
		html.Should().Contain("spec-link"); // operational contract documented
	}

	[Fact]
	public async Task Doc_Philosophy_IsPublic()
	{
		using var resp = await _client.GetAsync("/doc/methodology/philosophy");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		(await resp.Content.ReadAsStringAsync()).Should().Contain("doc-philosophy");
	}

	[Fact]
	public async Task Doc_Overview_IsPublic_ShowsModules()
	{
		using var resp = await _client.GetAsync("/doc/overview");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("doc-overview");
		html.Should().Contain("petbox-client"); // published client lib documented
		html.Should().Contain("What PetBox is</h1>"); // rendered from the md canon's H1, not hardcoded
		html.Should().NotContain("{{origin}}"); // the base-URL placeholder was substituted at render, not leaked
	}

	// The five /doc pages render their prose from the markdown canon (Pages/Doc/content/*.md) through
	// the shared server renderer (IMarkdownRenderer via _MdBody) — not hardcoded HTML. Proof: a known
	// H1 from each md file appears as a real closing <h1> in the initial response (so the file resolved
	// at the runtime path and went through the GFM renderer). Guards the doc-drift fix end to end.
	[Theory]
	[InlineData("/doc/overview", "What PetBox is</h1>")]
	[InlineData("/doc/agent", "Connect a coding agent to PetBox</h1>")]
	[InlineData("/doc/onboarding", "Agent onboarding</h1>")]
	[InlineData("/doc/methodology", "Methodology cheatsheet (agent)</h1>")]
	[InlineData("/doc/methodology/philosophy", "the model</h1>")]
	public async Task Doc_Pages_RenderMarkdownCanon_HeadingFromMd(string url, string heading)
	{
		using var resp = await _client.GetAsync(url);
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain(heading);
	}

	// The doc markdown canon MUST be present at the runtime content path (ContentRootPath-relative)
	// and load through DocContent with the dynamic substitution applied — otherwise /doc 500s for a
	// missing file in the published container. Guards the csproj <Content> copy + path resolution.
	[Fact]
	public void Doc_MarkdownCanon_ResolvesUnderContentRoot_AndSubstitutes()
	{
		var docs = _factory.Services.GetRequiredService<PetBox.Web.Pages.Doc.DocContent>();
		foreach (var slug in new[] { "overview", "agent", "onboarding", "methodology", "philosophy" })
			docs.Read(slug).Should().NotBeNullOrWhiteSpace($"{slug}.md must ship at the runtime doc path");
		// The origin placeholder is replaced in-place (proves the substitution mechanism runs).
		var overview = docs.Read("overview", new Dictionary<string, string> { ["origin"] = "https://example.test" });
		overview.Should().Contain("https://example.test").And.NotContain("{{origin}}");
	}

	[Fact]
	public async Task Doc_Onboarding_IsPublic_ShowsStagesAndGates()
	{
		using var resp = await _client.GetAsync("/doc/onboarding");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("doc-onboarding");
		html.Should().Contain("PETBOX_"); // per-project env-var step
		html.Should().Contain("specRef"); // spec-link step content
	}

	// ── ui-dashboard-zone-jumps ──────────────────────────────────────────────
	// The project dashboard's counter cards all point at the project (user) zone
	// /ui/{ws}/{key}/… read views (no jump to /ui/admin/…), the config card shows a
	// real number instead of the "cfg" literal, and the Tasks card is present.
	[Fact]
	public async Task ProjectDashboard_CountersTargetUserZone_ConfigIsNumber_TasksCardPresent()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();

		// logs / databases / config counters resolve to the user zone.
		html.Should().MatchRegex("""<a href="/ui/\$system/\$system/logs"[^>]*data-testid="count-logs""");
		html.Should().MatchRegex("""<a href="/ui/\$system/\$system/databases"[^>]*data-testid="count-dbs""");
		html.Should().MatchRegex("""<a href="/ui/\$system/\$system/config"[^>]*data-testid="count-config""");
		// The databases counter must NOT jump to the admin data page.
		html.Should().NotContain("/ui/admin/ws/$system/projects/$system/data");
		// Config card shows a number, not the old "cfg" literal.
		html.Should().NotContain(">cfg<");
		// Tasks card is present (Tasks feature is on in this fixture).
		html.Should().Contain("data-testid=\"count-tasks\"");
	}

	// Databases (user zone) empty state offers a real create link for an admin viewer,
	// not a dead-end sentence.
	[Fact]
	public async Task Databases_EmptyState_HasCreateLink()
	{
		using var resp = await GetAuthedAsync("/ui/$system/$system/databases");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().Contain("data-testid=\"databases-empty\"");
		html.Should().MatchRegex(
			"""<a href="/ui/admin/ws/\$system/projects/\$system/data"[^>]*data-testid="databases-create-link""");
	}

	// The workspace status dashboard's per-project db badge targets the user-zone
	// /databases read view, not the admin data page.
	[Fact]
	public async Task WorkspaceDashboard_DbBadge_TargetsUserZone_NotAdmin()
	{
		using var resp = await GetAuthedAsync("/ui/$system");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().MatchRegex("""<a href="/ui/\$system/\$system/databases"[^>]*data-testid="count-dbs""");
		html.Should().NotContain("/ui/admin/ws/$system/projects/$system/data");
	}

	// The sys dashboard: the Agent keys card is present, and the Defaults / Projects
	// cards are real links (were non-clickable divs).
	[Fact]
	public async Task SysDashboard_AgentKeysCard_And_DefaultsProjectsClickable()
	{
		using var resp = await GetAuthedAsync("/ui/admin/sys");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);
		var html = await resp.Content.ReadAsStringAsync();
		html.Should().MatchRegex("""<a href="/ui/admin/sys/agent-keys"[^>]*data-testid="sys-card-agent-keys""");
		html.Should().MatchRegex("""<a href="/ui/admin/sys/defaults"[^>]*data-testid="sys-card-defaults""");
		html.Should().MatchRegex("""<a href="/ui/admin/sys/workspaces"[^>]*data-testid="sys-card-projects""");
	}
}
