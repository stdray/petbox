using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// live-verification finding (owner, 2026-08-28): "mark waiting on me" (the decision-pending
// toggle, decision-pending-has-no-ui's only UI door) sat in the node-detail action row styled
// `btn-ghost` — at rest a ghost button has no background/border, so a multi-word label read as
// plain caption text rather than a control, unlike its neighbours. This is a GENERAL node-detail
// defect, not observation-specific (every board's node page shares this action row) — verified on
// a plain `work` board, not the `observations` board ObservationUiSignalsTests already covers.
//
// Fix: reused the row's ALREADY-established look for a SAME-CATEGORY control — a one-click
// POST-form submit button — rather than inventing a third style. The status-change "→" button
// (node-status-submit) is the other button in this row that is ALSO just a plain-submit form
// button (not a client-side toggle/modal-open like `edit`/`⤳ workflow`), and it was already
// `btn btn-xs` (no `-ghost`). Dropped `-ghost` from node-decision-pending-toggle to match.
//
// This asserts STRUCTURAL parity (no `btn-ghost` class) AND VISUAL parity (identical computed
// background-color via getComputedStyle) with node-status-submit — not just "has a btn class",
// which the ORIGINAL ghost-styled button already had and still looked like plain text.
//
// Checked for other elements in the same row sharing the disease: `⤳ workflow` and `edit` are
// BOTH still `btn-ghost` too, but they open a client-side modal / reveal an inline edit form —
// not a server-mutating submit — a different, lower-stakes category the ghost style already
// suits (same posture project-wide: every other "edit"-toggle button in Pages/ is ghost). Not
// touched; reported to the owner rather than changed silently, per the brief.
[Collection(nameof(UiCollection))]
public sealed class NodeActionRowAffordanceTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "row-affordance-ws";
	const string Proj = "row-affordance-proj";

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
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Row Affordance" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();
		if (!await tasks.BoardExistsAsync(Proj, "work"))
			await tasks.CreateBoardAsync(Proj, "work", "work", "work fixture", null, methodologyInstance: TaskBoardMeta.UtilityWorld);
		var existing = await tasks.GetAsync(Proj, "work", includeClosed: true);
		if (existing.Nodes.Count == 0)
			// Pending (birth status) -> a chore has legal NextStatuses (InProgress/Cancelled), so
			// the status-change "→" submit button — the affordance reference point — actually renders.
			await tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = "row-node", Version = 0, Type = "chore", Title = "Row node", Body = "x" }]);

		_nodeUrl = $"/ui/{Ws}/{Proj}/tasks/work/row-node";
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
	public async Task DecisionPendingToggle_MatchesStatusSubmitButtonAffordance_NotGhostStyled()
	{
		await _page!.GotoAsync(_nodeUrl);

		var toggle = _page.GetByTestId("node-decision-pending-toggle");
		var statusSubmit = _page.GetByTestId("node-status-submit");
		await Expect(toggle).ToBeVisibleAsync();
		await Expect(statusSubmit).ToBeVisibleAsync();

		// Structural: same button family (btn + size), no ghost modifier on either.
		var toggleClass = await toggle.GetAttributeAsync("class") ?? "";
		var statusSubmitClass = await statusSubmit.GetAttributeAsync("class") ?? "";
		toggleClass.Should().Contain("btn").And.NotContain("btn-ghost");
		statusSubmitClass.Should().Contain("btn").And.NotContain("btn-ghost");

		// Visual: identical rendered affordance, not just a shared substring in the class list —
		// this is the assertion that would have caught the original defect (the old `btn-ghost`
		// class list also technically "contained btn", but rendered with no visible box at all).
		var toggleBg = await toggle.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");
		var statusSubmitBg = await statusSubmit.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor");
		var toggleBorder = await toggle.EvaluateAsync<string>("el => getComputedStyle(el).borderColor");
		var statusSubmitBorder = await statusSubmit.EvaluateAsync<string>("el => getComputedStyle(el).borderColor");
		toggleBg.Should().Be(statusSubmitBg);
		toggleBorder.Should().Be(statusSubmitBorder);

		// The two other action-row controls that remain ghost-styled by DESIGN (client-side
		// toggle/reveal, not a server-mutating submit) — asserts the finding stays true rather
		// than silently drifting, not that they should change.
		var workflowClass = await _page.GetByTestId("workflow-open").GetAttributeAsync("class") ?? "";
		var editClass = await _page.GetByTestId("node-edit-toggle").GetAttributeAsync("class") ?? "";
		workflowClass.Should().Contain("btn-ghost");
		editClass.Should().Contain("btn-ghost");
	}
}
