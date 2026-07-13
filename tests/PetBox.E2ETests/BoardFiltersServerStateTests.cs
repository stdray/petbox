using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;
using PetBox.Web.Rendering;
using PetBox.Web.Settings;

namespace PetBox.E2ETests;

// work `board-filters-server-state`: active-only / sort (DB [Setting], BoardPreferences) and the
// collapsed-node set (cookie, BrowserState.CollapsedByBoard) used to be applied by
// ts/board.ts's initBoardPage AFTER paint, on every single load — the server always sent
// unfiltered/unsorted/fully-expanded rows. This asserts the raw HTTP response body (never the
// post-hydration DOM — see SidebarPinTests.Server_Renders_Correct_DrawerClass_In_The_First_Response
// for why that distinction is the actual regression guard) already reflects all three before any
// script has run.
[Collection(nameof(UiCollection))]
public sealed class BoardFiltersServerStateTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "boardfilters-ws";
	const string Proj = "boardfilters-proj";
	const string Board = "filtersboard";

	IBrowserContext? _ctx;
	IPage? _page;
	string _parentNodeId = "";
	// active-only/sort are the admin's GLOBAL board preference (deliberately board-independent —
	// see BoardPreferences.cs) — this test seeds a non-default value onto the SAME shared admin
	// user every other E2E class in this collection also authenticates as. Captured here and
	// restored in DisposeAsync so this test doesn't leak a global default change to a sibling test
	// class that assumes the untouched default (e.g. CustomKindBoardTests' active-only-hides-
	// terminal-node assertion) and happens to run later in the same collection.
	BoardPreferences? _originalBoardPrefs;
	string _adminUserIdString = "";
	long _adminUserId;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Board Filters Server State" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "board-filters-server-state fixture", null, null);

		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, Board,
			[
				new NodePatch { Key = "alpha", Title = "Alpha", Priority = 50, Status = "Todo" },
				new NodePatch { Key = "beta", Title = "Beta Done Task", Priority = 10, Status = "Done" }, // terminal — closed
			]);
			await tasks.UpsertAsync(Proj, Board,
				[new NodePatch { Key = "child", Title = "Child Of Alpha", Priority = 5, Status = "Todo", PartOf = "alpha" }]);
		}
		var reloaded = await tasks.GetAsync(Proj, Board, includeClosed: true);
		_parentNodeId = reloaded.Nodes.Single(n => n.Key == "alpha").NodeId;

		// Seed the GLOBAL, cross-device DB preference directly (this is the admin user's own row —
		// the only user this E2E suite authenticates as) — same effect as a prior POST to
		// /api/ui/board-filter-prefs, without needing a second page/fetch round trip in the fixture.
		var admin = await db.Users.SingleAsync(u => u.Username == WebAppFixture.AdminUsername);
		var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
		_adminUserId = admin.Id;
		_adminUserIdString = admin.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
		_originalBoardPrefs = await settings.GetAsync<BoardPreferences>(Scope.User, _adminUserIdString);
		await settings.SetAsync(Scope.User, _adminUserIdString,
			_originalBoardPrefs with { ActiveOnly = false, SortBy = BoardSortKeys.Title, SortDesc = true },
			_originalBoardPrefs, admin.Id);

		_ctx = await app.NewContextAsync(authenticated: true);
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null) { await TraceArtifact.StopAndSaveAsync(_ctx, output); await _ctx.CloseAsync(); }

		if (_originalBoardPrefs is not null)
		{
			using var scope = app.Services.CreateScope();
			var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
			var current = await settings.GetAsync<BoardPreferences>(Scope.User, _adminUserIdString);
			await settings.SetAsync(Scope.User, _adminUserIdString, _originalBoardPrefs, current, _adminUserId);
		}
	}

	static string BoardUrl => $"/ui/{Ws}/{Proj}/tasks/{Board}";

	[Fact]
	public async Task ActiveOnlyAndSort_ResolveInTheFirstResponse_NoQueryString()
	{
		_page = await _ctx!.NewPageAsync();
		var response = await _page.GotoAsync(BoardUrl);
		var html = await response!.TextAsync();

		// active-only is FALSE (the seeded preference) — the closed "beta" node must be present and
		// NOT display:none, and the checkbox itself renders unchecked, both from the FIRST response.
		html.Should().MatchRegex("data-node-key=\"beta\"[^>]*style=\"[^\"]*display:\"",
			"the closed node must still be present with an EMPTY display value (not \"none\") since active-only resolved to false server-side");
		Regex.IsMatch(html, "data-testid=\"active-only-toggle\"[^>]*checked=\"checked\"").Should().BeFalse(
			"active-only is off — the checkbox must render UNCHECKED in the first response, not corrected by JS after paint");

		// sort = title desc: root siblings "Beta Done Task" and "Alpha" order Beta before Alpha
		// (descending title) — assert via each row's position in the raw HTML.
		var betaIdx = html.IndexOf("data-node-key=\"beta\"", StringComparison.Ordinal);
		var alphaIdx = html.IndexOf("data-node-key=\"alpha\"", StringComparison.Ordinal);
		betaIdx.Should().BeGreaterThan(-1);
		alphaIdx.Should().BeGreaterThan(-1);
		betaIdx.Should().BeLessThan(alphaIdx,
			"title-descending sort ('Beta Done Task' > 'Alpha') must already be the server's DFS sibling order in the first response");

		// The sort controls themselves reflect the resolved preference (checked/selected/arrow),
		// not just the row order — the same "render, don't correct" contract as active-only above.
		html.Should().Contain("<option value=\"title\" selected=\"selected\">title</option>");
		// Razor's default HtmlEncoder escapes non-ASCII expression output to a numeric character
		// reference (confirmed: "&#x2193;" for ↓, U+2193) — a browser renders it identically, but
		// the raw-body assertion has to look for the actual bytes the server sent.
		Regex.IsMatch(html, "data-testid=\"board-sort-dir\"[^>]*>&#x2193;").Should().BeTrue(
			"the descending arrow (↓, encoded as &#x2193; by Razor) must render server-side, matching the saved SortDesc:true");
	}

	[Fact]
	public async Task CollapsedSet_HidesChild_InTheFirstResponse_ViaCookie_NoLocalStorage()
	{
		// The collapsed cookie is set directly on the context — no JS ever ran to produce it, the
		// same technique SidebarPinTests.Server_Renders_Correct_DrawerClass_In_The_First_Response
		// uses for BrowserState.SidebarPinned.
		var cookieValue = "{\"collapsedByBoard\":{\"" + Proj + "/" + Board + "\":[\"" + _parentNodeId + "\"]}}";
		await _ctx!.AddCookiesAsync(
		[
			new Cookie { Name = UiStateResolver.CookieName, Value = Uri.EscapeDataString(cookieValue), Url = app.BaseUrl },
		]);

		_page = await _ctx.NewPageAsync();
		var response = await _page.GotoAsync(BoardUrl);
		var html = await response!.TextAsync();

		html.Should().MatchRegex("data-node-key=\"child\"[^>]*style=\"[^\"]*display:none",
			"the child's parent is in the collapsed set for this board — the row must already be display:none in the FIRST response, not hidden by a script after paint");
		// The collapse caret itself reflects the collapsed state (▸, not ▾) server-side.
		html.Should().Contain("data-collapsed=\"true\"");
	}
}
