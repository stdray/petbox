using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// The global cross-scope task search box (spec cross-scope-task-search): the top-nav box
// (_Layout, data-testid="nav-search") must be present on any authed page, and submitting a
// slug that lives in a DIFFERENT workspace/project than the one currently open must still
// find it and link straight to its node page. Seeds two workspaces + two projects + a task
// node directly via the fixture's service provider (there's no UI board-creation flow —
// boards are agent-created via the tasks MCP tools), then drives the box like a user would.
[Collection(nameof(UiCollection))]
public sealed class CrossScopeSearchTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string WsA = "xscope-ws-a";
	const string WsB = "xscope-ws-b";
	const string ProjA = "xscope-proj-a";
	const string ProjB = "xscope-proj-b";
	const string Board = "work";
	static readonly string Slug = "xscope-target-" + Guid.NewGuid().ToString("N")[..8];

	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();

		foreach (var ws in new[] { WsA, WsB })
			if (!await db.Workspaces.AnyAsync(w => w.Key == ws))
				await db.InsertAsync(new Workspace { Key = ws, Name = ws, CreatedAt = DateTime.UtcNow });

		if (!await db.Projects.AnyAsync(p => p.Key == ProjA))
			await db.InsertAsync(new Project { Key = ProjA, WorkspaceKey = WsA, Name = "XScope A" });
		if (!await db.Projects.AnyAsync(p => p.Key == ProjB))
			await db.InsertAsync(new Project { Key = ProjB, WorkspaceKey = WsB, Name = "XScope B" });

		// The target node lives in project B / workspace B — a workspace the "current" page
		// (workspace A) is NOT scoped to, proving the search really fans out cross-workspace.
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		await tasks.UpsertAsync(ProjB, Board,
		[
			new NodePatch { Key = Slug, Title = "Cross-scope target node", Body = "Findable from anywhere." },
		]);

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	public async Task DisposeAsync()
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
}
