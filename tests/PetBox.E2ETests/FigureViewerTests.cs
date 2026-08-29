using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// Figure viewer (spec `body-figure-inspectable`): a figure embedded in a body can be viewed
// enlarged. The whole feature is client-side (ts/figure-viewer.ts) — the SVG arrives as ordinary
// sanitized inline markup in the node body, so this test seeds a node whose body carries an
// inline-SVG figure, then drives the corner "⛶" trigger like a user would and asserts the MOVE
// semantics around the native <dialog>: the svg is MOVED into the dialog (not cloned — the body
// holds zero svgs while it is open) and restored to its original spot on close. The decorator's
// selection logic (which elements get a trigger) is covered by the bun unit test alongside the
// module; jsdom cannot run showModal, so the dialog mechanics only verify here.
[Collection(nameof(UiCollection))]
public sealed class FigureViewerTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "fig-viewer-ws";
	const string Proj = "fig-viewer-proj";

	// Raw HTML a body legitimately carries (spec `body-carries-diagram`): everything here is on
	// the sanitizer's SVG allowlist, so the figure survives the render pipeline as-is.
	const string Body = """
		<p>Diagram below.</p>
		<figure>
		<svg viewBox="0 0 120 60" role="img"><rect x="2" y="2" width="116" height="56" fill="none" stroke="currentColor"></rect><text x="60" y="34" text-anchor="middle">BRIDGE</text></svg>
		<figcaption>the reference diagram</figcaption>
		</figure>
		""";

	IBrowserContext? _ctx;
	IPage? _page;
	string _nodeUrl = "";

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Figure Viewer" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, "work"))
			await tasks.CreateBoardAsync(Proj, "work", "work", "work fixture", null, methodologyInstance: TaskBoardMeta.UtilityWorld);
		var existing = await tasks.GetAsync(Proj, "work", includeClosed: true);
		if (existing.Nodes.Count == 0)
			await tasks.UpsertAsync(Proj, "work",
				[new NodePatch { Key = "fig-node", Version = 0, Type = "chore", Title = "Figure node", Body = Body }]);

		_nodeUrl = $"/ui/{Ws}/{Proj}/tasks/work/fig-node";
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
	public async Task FigureTrigger_OpensDialogWithMovedSvg_ClosesAndRestores()
	{
		await _page!.GotoAsync(_nodeUrl);

		var body = _page.GetByTestId("node-body");
		var trigger = _page.GetByTestId("figure-viewer-trigger");
		await Expect(trigger).ToHaveCountAsync(1);
		await Expect(body.Locator("svg")).ToHaveCountAsync(1);
		var dialog = _page.GetByTestId("figure-viewer-dialog");

		await trigger.ClickAsync();

		await Expect(dialog).ToBeVisibleAsync();
		// The svg (and its caption) are IN the dialog — moved there, not cloned.
		await Expect(dialog.Locator("svg")).ToHaveCountAsync(1);
		await Expect(dialog).ToContainTextAsync("the reference diagram");
		await Expect(body.Locator("svg")).ToHaveCountAsync(0);

		await _page.GetByTestId("figure-viewer-close").ClickAsync();

		// NOT ToBeHiddenAsync: daisyUI 4's closed .modal is opacity-0 but still display:grid —
		// a state Playwright reads as visible (the exact trap WorkflowVizTests documents). The
		// open STATE is the honest signal, alongside the functional one (restoration below).
		(await dialog.EvaluateAsync<bool>("d => d.open")).Should().BeFalse();
		await Expect(body.Locator("svg")).ToHaveCountAsync(1);
		await Expect(body).ToContainTextAsync("the reference diagram");
	}
}
