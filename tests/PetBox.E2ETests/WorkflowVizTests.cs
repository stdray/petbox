using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// "View workflow" graph modal (per-type-workflow-graph-viz): landing on a board of an unfamiliar
// methodology, a user clicks the type's "workflow" trigger and sees the FSM as an SVG graph —
// statuses as nodes, transitions as edges, gates as labels. Seeds an `intake` board directly via
// the tasks service (no UI board-creation flow), then drives the trigger like a user would and
// asserts the modal renders the expected status nodes. Boots on the freshly-bundled site.js.
[Collection(nameof(UiCollection))]
public sealed class WorkflowVizTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "wfviz-ws";
	const string Proj = "wfviz-proj";
	const string Board = "intake";

	IBrowserContext? _ctx;
	IPage? _page;

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Workflow Viz" });

		// An `intake` board: a compact, gated FSM (reported→triage→confirmed→done, plus
		// triage→duplicate/wontfix) — exactly the "unfamiliar methodology" the viz is for.
		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, Board))
			await tasks.CreateBoardAsync(Proj, Board, "intake", "workflow viz fixture", null);

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
	public async Task BoardPage_WorkflowTrigger_OpensModalWithStatusGraph()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{Board}");
		await Expect(_page.GetByTestId("board-kind")).ToContainTextAsync("intake");

		// The graph is rendered on open (and cleared on close) — a reliable open/closed signal that
		// doesn't depend on daisyUI's opacity-only closed state (which Playwright reads as visible).
		var modal = _page.GetByTestId("workflow-modal");
		var svg = _page.GetByTestId("workflow-svg");
		await Expect(svg).ToHaveCountAsync(0);

		await _page.GetByTestId("workflow-open").First.ClickAsync();

		await Expect(modal).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("workflow-modal-title")).ToContainTextAsync("issue");

		// Hand-rolled SVG (no diagram library); its status nodes carry the FSM vocabulary.
		await Expect(svg).ToBeVisibleAsync();
		await Expect(modal).ToContainTextAsync("Reported");
		await Expect(modal).ToContainTextAsync("Confirmed");
		await Expect(modal).ToContainTextAsync("Done");

		// Closing the modal tears down the graph.
		await _page.GetByTestId("workflow-modal-close").ClickAsync();
		await Expect(svg).ToHaveCountAsync(0);
	}
}
