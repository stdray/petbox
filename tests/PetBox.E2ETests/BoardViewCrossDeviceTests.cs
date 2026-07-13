using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// work `board-view-cross-device`: the board's view mode used to live in
// localStorage['tasksView:<project>/<board>'], per-browser, with a client reconcile that did
// `window.location.replace()` on a mismatch (one wrong paint + a SECOND full page load). It is now
// a per-(project,board) DB [Setting] (BoardPreferences.ViewPreferences, Scope.User) — resolved
// server-side before render, exactly like sidebar-pin-server-state's fix for the drawer class.
//
// Two SEPARATE IBrowserContext instances from WebAppFixture.NewContextAsync(authenticated:true)
// share the SAME logged-in user (one shared Playwright storageState) but have their OWN, entirely
// independent cookie jars and no shared localStorage — this is exactly "the same person's two
// devices" without needing a second real machine.
[Collection(nameof(UiCollection))]
public sealed class BoardViewCrossDeviceTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "boardviewxdev-ws";
	const string Proj = "boardviewxdev-proj";
	const string Board = "xdevboard";

	IBrowserContext? _ctxA;
	IBrowserContext? _ctxB;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Board View Cross-Device" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "simple", "board-view-cross-device fixture", null, null);
		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
			await tasks.UpsertAsync(Proj, Board, [new NodePatch { Key = "n1", Title = "N1", Body = "x", Priority = 10 }]);

		_ctxA = await app.NewContextAsync(authenticated: true);
		_ctxB = await app.NewContextAsync(authenticated: true);
	}

	public async Task DisposeAsync()
	{
		if (_ctxA is not null) { await TraceArtifact.StopAndSaveAsync(_ctxA, output); await _ctxA.CloseAsync(); }
		if (_ctxB is not null) { await TraceArtifact.StopAndSaveAsync(_ctxB, output); await _ctxB.CloseAsync(); }
	}

	static string BoardUrl => $"/ui/{Ws}/{Proj}/tasks/{Board}";

	// THE cross-device proof. Device A picks kanban via the explicit, shareable `?view=` link.
	// Device B — a totally separate cookie jar, no localStorage carried over, no query string at
	// all — must ALREADY render kanban on its own FIRST response. Asserting on IResponse.TextAsync()
	// (the raw bytes the server sent) rather than the post-hydration DOM is what actually proves no
	// redirect happened: the old window.location.replace() would still leave the SECOND response
	// looking identical to a DOM-only check, since by the time Chromium finishes painting, the
	// redirect has already landed.
	[Fact]
	public async Task ViewMode_ChosenOnOneDevice_RendersOnAnother_FirstResponse_NoRedirect()
	{
		var pageA = await _ctxA!.NewPageAsync();
		await pageA.GotoAsync($"{BoardUrl}?view=kanban");
		await Expect(pageA.GetByTestId("board-kanban")).ToBeVisibleAsync();

		var pageB = await _ctxB!.NewPageAsync();
		var response = await pageB.GotoAsync(BoardUrl); // no query string — device B's first-ever visit
		var html = await response!.TextAsync();

		html.Should().Contain("data-testid=\"board-kanban\"",
			"the saved per-(project,board) DB preference must already resolve to kanban in the FIRST response on a different device/context — no client JS ran to produce this HTML");
		html.Should().NotContain("data-testid=\"board-nodes\"", "the tree pane must NOT also be present — kanban is what actually rendered");

		// No redirect: the URL Playwright ended up on is still the plain board URL, no ?view= was
		// appended by a client-side reconcile (there is no such mechanism anymore).
		new Uri(pageB.Url).Query.Should().BeEmpty("a plain visit must never gain a query string from a server or client redirect");
	}

	[Fact]
	public async Task FieldSelection_ChosenOnOneDevice_RendersOnAnother_FirstResponse()
	{
		var pageA = await _ctxA!.NewPageAsync();
		await pageA.GotoAsync($"{BoardUrl}?fields=type&fields=priority&fieldsSet=1");
		await Expect(pageA.GetByTestId("node-type").First).ToBeVisibleAsync();

		var pageB = await _ctxB!.NewPageAsync();
		var response = await pageB.GotoAsync(BoardUrl);
		var html = await response!.TextAsync();

		html.Should().Contain("data-testid=\"node-type\"",
			"the saved field selection (type+priority) must already apply on device B's first response");
		html.Should().Contain("data-testid=\"node-priority\"");
		html.Should().NotContain("data-testid=\"node-tags\"", "tags was not part of the saved selection");
	}
}
