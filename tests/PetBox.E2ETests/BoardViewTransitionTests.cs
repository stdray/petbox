using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// board-ui-review-findings #3: switching board view mode (table <-> kanban <-> tree <-> outline)
// visibly flickered the sidebar. DIAGNOSIS (see ts/app.css's own comment for the full writeup):
// the naive "JS strips the class after load" explanation from the PRE-ui-state-framework world no
// longer holds — sidebar-pin-server-state already made the drawer-open class server-rendered from
// the `petbox.ui` cookie before the first byte goes out, and board-view-cross-device already
// removed the client redirect, so every view switch is a single plain `<a href>` navigation with a
// CORRECT first response. Confirmed empirically (raw HTTP diff, table vs kanban): the drawer class
// is byte-identical on both responses, and the kanban-body-scrolls-not-ribbon min-w-0 fix already
// keeps a wide ribbon/table from reflowing the drawer. What was left is NOT a bug in this app's
// server or client code at all: a cross-document navigation is, by default, a hard cut to blank
// before the new document paints — repainting chrome that never actually changed (the sidebar is
// pixel-identical across every view of the same board), which is exactly what reads as "disappear
// and reappear". The fix opts the app into the browser-native View Transition API for cross-document
// navigations (`@view-transition { navigation: auto; }` in app.css) — old/new page snapshots
// crossfade instead of hard-cutting, so an unchanged region (the sidebar) renders visually static
// through the transition. This test proves the opt-in actually ENGAGES a transition on a real view
// switch (pageswap/pagereveal's `viewTransition` is non-null only when the browser is actually
// running one) — a DOM-only assertion could not tell "hard cut" from "crossfade" apart, since the
// FINAL DOM is identical either way; this is the closest objective, automatable proxy for "the
// flicker is gone" that a CI-run browser (Chromium — the same engine Playwright/this project's own
// E2E suite always uses) can report.
[Collection(nameof(UiCollection))]
public sealed class BoardViewTransitionTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "boardvt-ws";
	const string Proj = "boardvt-proj";
	const string Board = "vtboard";

	IBrowserContext? _ctx;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Board View Transition" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "board-view-transition fixture", null, null);
		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
			await tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = "n1", Title = "N1", Body = "x", Priority = 10 }]);

		_ctx = await app.NewContextAsync(authenticated: true);
	}

	public async Task DisposeAsync()
	{
		if (_ctx is not null) { await TraceArtifact.StopAndSaveAsync(_ctx, output); await _ctx.CloseAsync(); }
	}

	static string BoardUrl => $"/ui/{Ws}/{Proj}/tasks/{Board}";

	[Fact]
	public async Task Switching_View_Engages_A_Native_CrossDocument_ViewTransition()
	{
		var page = await _ctx!.NewPageAsync();

		// Re-injected before every navigation (a fresh document has no JS state of its own) —
		// records whether the OUTGOING document's unload (`pageswap`) and the INCOMING document's
		// first paint (`pagereveal`) each carried a live ViewTransition object. sessionStorage
		// (unlike page-local JS variables) survives the navigation so the flags can be read back
		// from the destination page.
		await _ctx.AddInitScriptAsync("""
			window.addEventListener('pageswap', (e) => {
				try { sessionStorage.setItem('e2eVtSwap', String(!!e.viewTransition)); } catch (_) {}
			});
			window.addEventListener('pagereveal', (e) => {
				try { sessionStorage.setItem('e2eVtReveal', String(!!e.viewTransition)); } catch (_) {}
			});
			""");

		await page.GotoAsync($"{BoardUrl}?view=table");
		await page.EvaluateAsync("() => { sessionStorage.removeItem('e2eVtSwap'); sessionStorage.removeItem('e2eVtReveal'); }");

		await page.GetByTestId("view-kanban").ClickAsync();
		await Expect(page.GetByTestId("board-kanban")).ToBeVisibleAsync();

		var swapEngaged = await page.EvaluateAsync<string?>("() => sessionStorage.getItem('e2eVtSwap')");
		var revealEngaged = await page.EvaluateAsync<string?>("() => sessionStorage.getItem('e2eVtReveal')");

		swapEngaged.Should().Be("true", "the outgoing table-view document must hand off a live ViewTransition on navigate-away — app.css's `@view-transition` rule (navigation:auto) opts every cross-document nav into this");
		revealEngaged.Should().Be("true", "the incoming kanban-view document must reveal through that SAME ViewTransition, not a hard cut — this is what keeps the unchanged sidebar visually static instead of blinking out");
	}
}
