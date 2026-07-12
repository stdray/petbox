using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.E2ETests.Infrastructure;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.E2ETests;

// ui-methodology-runtime-unify: a board of a DEFINITION-declared custom kind must render in the
// UI the same way it behaves on MCP — the board page resolves kind/terminality through
// MethodologyRuntime, not the MethodologyPresets statics that collapse a custom kind to `Simple`.
// Seeds (directly via the tasks service — no UI provisioning flow) a project with a custom
// methodology definition, a board of that kind, and two nodes: one OPEN and one in a CUSTOM
// TERMINAL status. Then opens the board and asserts the kind badge shows the custom slug and the
// terminal node is hidden by the active-only default (board.ts hides data-closed rows) while the
// open node stays — proving the runtime, not the preset fallback, classified the custom terminal.
[Collection(nameof(UiCollection))]
public sealed class CustomKindBoardTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "customkind-ws";
	const string Proj = "customkind-proj";
	const string Board = "risks";

	IBrowserContext? _ctx;
	IPage? _page;

	// A custom `risk` kind: quick-add off, its own vocab with a terminal `Mitigated` (terminalok)
	// the built-in presets don't know — the exact slug the preset fallback would misclassify.
	static MethodologyDefinition RiskDefinition() => new(
		"acme-risk",
		[
			new MethodologyKindDef(
				Kind: "risk",
				QuickAddAllowed: false,
				Workflows:
				[
					new MethodologyWorkflowDef(
						Types: ["risk"],
						Statuses:
						[
							new WorkflowStatus("Open", "Open", StatusKind.Open),
							new WorkflowStatus("Mitigated", "Mitigated", StatusKind.TerminalOk),
						],
						Transitions:
						[
							new MethodologyTransitionDef("Open", "Mitigated"),
						]),
				]),
		]);

	public async Task InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();

		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Custom Kind" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		// Live instance FIRST (so `risk` is a known kind via instance rules), then nodes on
		// the provisioned board: one open (visible), one born in the custom terminal status.
		if (await tasks.GetMethodologyInstanceAsync(Proj, "risks") is null)
		{
			await tasks.UpsertMethodologyTemplateAsync(Proj, "risk-tmpl", RiskDefinition(), 0);
			await tasks.CreateMethodologyInstanceAsync(Proj, "risks", "template", "risk-tmpl");
		}
		// Single-kind instance names its board after the instance ("risks").
		if (!await tasks.BoardExistsAsync(Proj, Board))
		{
			await tasks.CreateBoardAsync(Proj, Board, "risk", "custom kind fixture", null, "risks");
		}
		var existing = await tasks.GetAsync(Proj, Board, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			await tasks.UpsertAsync(Proj, Board,
			[
				new NodePatch { Key = "r-open", Type = "risk", Status = "Open", Title = "Open risk", Body = "x" },
				new NodePatch { Key = "r-mit", Type = "risk", Status = "Mitigated", Title = "Mitigated risk", Body = "x" },
			]);
		}

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
	public async Task BoardPage_CustomKind_BadgeNamesKind_AndTerminalNodeHiddenByActiveOnly()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{Board}");

		// The kind badge names the DEFINITION kind, not the `simple` preset fallback.
		await Expect(_page.GetByTestId("board-kind")).ToContainTextAsync("risk");

		// Both cards are server-rendered (includeClosed); the OPEN one stays visible…
		await Expect(_page.Locator("[data-node-key='r-open']")).ToBeVisibleAsync();
		// …and the custom-TERMINAL one is hidden by the active-only default — which only works if
		// the page classified `Mitigated` as terminal via the runtime (the preset fallback would
		// leave data-closed=false and the row would stay visible).
		await Expect(_page.Locator("[data-node-key='r-mit']")).ToBeHiddenAsync();
		await Expect(_page.Locator("[data-node-key='r-mit']")).ToHaveAttributeAsync("data-closed", "true");

		// Quick-add is gated off for this kind (its definition sets quickAddAllowed:false).
		await Expect(_page.GetByTestId("task-create")).ToHaveCountAsync(0);
	}
}
