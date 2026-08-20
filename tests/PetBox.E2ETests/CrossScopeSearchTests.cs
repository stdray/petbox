using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// The global cross-scope task search box (spec cross-scope-task-search): the top-nav box
// (_Layout, data-testid="nav-search") must be present on any authed page, and submitting a
// slug that lives in a DIFFERENT workspace/project than the one currently open must still
// find it and link straight to its node page. Seeds workspaces + projects + a task node
// directly via the fixture's service provider (there's no UI board-creation flow —
// boards are agent-created via the tasks MCP tools), then drives the box like a user would.
//
// MEMBERSHIP IS PART OF THE SETUP, since spec tenant-visibility-by-membership. /ui/search is a
// user-zone route, so its fan-out is the caller's MEMBERSHIPS — the fixture's `admin` account holds
// the system permission, and that permission is deliberately no longer a listing gate here. So the
// positive test grants the account membership of both workspaces: the search still crosses a
// workspace boundary (which is the thing this file exists to prove), it just crosses one the caller
// belongs to on both sides. WsC is seeded WITHOUT membership and stays invisible — that is the
// negative case, and it is the /ui/search half of the same spec.
[Collection(nameof(UiCollection))]
public sealed class CrossScopeSearchTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string WsA = "xscope-ws-a";
	const string WsB = "xscope-ws-b";
	const string WsC = "xscope-ws-c";
	const string ProjA = "xscope-proj-a";
	const string ProjB = "xscope-proj-b";
	const string ProjC = "xscope-proj-c";
	const string Board = "work";
	static readonly string Slug = "xscope-target-" + Guid.NewGuid().ToString("N")[..8];
	static readonly string ForeignSlug = "xscope-foreign-" + Guid.NewGuid().ToString("N")[..8];

	IBrowserContext? _ctx;
	IPage? _page;

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		foreach (var ws in new[] { WsA, WsB, WsC })
			if (!await db.Workspaces.AnyAsync(w => w.Key == ws))
				await db.InsertAsync(new Workspace { Key = ws, Name = ws, CreatedAt = DateTime.UtcNow });

		if (!await db.Projects.AnyAsync(p => p.Key == ProjA))
			await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = WsA, Name = "XScope A" });
		if (!await db.Projects.AnyAsync(p => p.Key == ProjB))
			await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = WsB, Name = "XScope B" });
		if (!await db.Projects.AnyAsync(p => p.Key == ProjC))
			await db.InsertAsync(new Project { Key = ProjC, WorkspaceKey = WsC, Name = "XScope C" });

		// The signed-in account joins A and B — and pointedly NOT C. Since tenant-visibility-by-
		// membership the user zone enumerates by membership, so without these rows the fan-out below
		// would legitimately see nothing: holding the system permission stopped being a listing gate.
		// WorkspaceClaimsRefresher rebuilds yb:ws_roles from these rows on every authenticated
		// request, so they take effect on the fixture's existing cookie with no re-login.
		// Through IWorkspaceMembershipService, the production path — a raw insert here is banned
		// (RS0030) precisely because seeding around the service is how membership bugs stay green.
		// AddMemberAsync is idempotent for an account that is already a member, so re-running is safe.
		var members = scope.ServiceProvider.GetRequiredService<IWorkspaceMembershipService>();
		foreach (var ws in new[] { WsA, WsB })
			await members.AddMemberAsync(ws, WebAppFixture.AdminUsername, password: null, WorkspaceRole.Admin);

		// The target node lives in project B / workspace B — a workspace the "current" page
		// (workspace A) is NOT scoped to, proving the search really fans out cross-workspace.
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.UpsertAsync(ProjB, Board,
		[
			new NodePatch { Key = Slug, Title = "Cross-scope target node", Body = "Findable from anywhere." },
		]);

		// ...and this one lives in workspace C, which the account is NOT a member of.
		await tasks.UpsertAsync(ProjC, Board,
		[
			new NodePatch { Key = ForeignSlug, Title = "Foreign tenant node", Body = "Must not surface in /ui/search." },
		]);

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async ValueTask DisposeAsync()
	{
		if (_ctx is not null)
		{
			await TraceArtifact.StopAndSaveAsync(_ctx, output);
			await _ctx.CloseAsync();
		}
	}

	[Fact]
	public async Task NavSearchBox_IsPresentOnAnAuthedPage()
	{
		await _page!.GotoAsync($"/ui/{WsA}");
		await Expect(_page.GetByTestId("nav-search")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("nav-search-input")).ToBeVisibleAsync();
	}

	[Fact]
	public async Task SubmittingASlug_FromADifferentWorkspace_FindsItAndNavigatesToTheNode()
	{
		// Land on workspace A — the slug we're about to search for lives in workspace B.
		await _page!.GotoAsync($"/ui/{WsA}");

		await _page.GetByTestId("nav-search-input").FillAsync(Slug);
		await _page.GetByTestId("nav-search-input").PressAsync("Enter");
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		_page.Url.Should().Contain("/ui/search");
		_page.Url.Should().Contain(Uri.EscapeDataString(Slug));

		var hit = _page.GetByTestId("search-hit").Filter(new() { HasText = "Cross-scope target node" });
		await Expect(hit).ToBeVisibleAsync();
		// The row names WHERE the task lives — a different workspace/project than the current one.
		// (board-view-mode-framework: the result table's workspace/project/board columns replaced
		// the earlier grouped-by-workspace/project sections — same information, one row now.)
		await Expect(hit).ToContainTextAsync(WsB);
		await Expect(hit).ToContainTextAsync(ProjB);

		await hit.Locator("a").ClickAsync();
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		_page.Url.Should().Contain($"/ui/{WsB}/{ProjB}/tasks/{Board}/{Slug}");
		await Expect(_page.GetByTestId("node-name")).ToContainTextAsync("Cross-scope target node");
	}

	// Criterion 4 of user-zone-tenant-visibility, end to end and through the real stack: the SAME
	// signed-in account holds the system permission (it is the bootstrap admin — note the
	// "Administration" link in its own chrome) and is therefore entitled to OPEN workspace C. Its
	// user-zone search still must not return C's nodes: the fan-out follows membership, not
	// permission. The pasted slug is exact and unique, so a miss here can only be the scoping.
	[Fact]
	public async Task SubmittingASlug_FromAWorkspaceTheAccountIsNotAMemberOf_FindsNothing()
	{
		await _page!.GotoAsync($"/ui/{WsA}");

		await _page.GetByTestId("nav-search-input").FillAsync(ForeignSlug);
		await _page.GetByTestId("nav-search-input").PressAsync("Enter");
		await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

		_page.Url.Should().Contain("/ui/search");
		await Expect(_page.GetByTestId("search-hit").Filter(new() { HasText = "Foreign tenant node" }))
			.ToHaveCountAsync(0);

		// ...and the right itself is untouched: the very same node opens when ADDRESSED directly.
		// Visibility by default changed; access did not (spec workspace-read-isolation stands).
		await _page.GotoAsync($"/ui/{WsC}/{ProjC}/tasks/{Board}/{ForeignSlug}");
		await Expect(_page.GetByTestId("node-name")).ToContainTextAsync("Foreign tenant node");
	}
}
