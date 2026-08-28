using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Contract;

namespace PetBox.E2ETests;

// work observation-ui-shows-recurrence-and-regression: the UI reuses the whole task board/node
// scaffolding for the `observations` board (kind `observation`, system builtin) — right, but
// until this card nothing distinguished an observation card from a plain task card. The signal
// was already computed and already shipped on the wire (TaskNodeView.Observation, spec
// observation-recurrence-after-fix-signal); nothing new is COMPUTED here, only SHOWN, in three
// shared partials so the board card (_TaskNodeCard), the flat table row (_TaskTable) and the node
// detail page (TaskBoardNode) render it identically:
//   - spec observation-recurrence-visible-on-card: a ×N + last-seen badge once RecurrenceCount > 1.
//   - spec observation-regression-signalled-on-card: a SEPARATE, noticeable alert banner ("recurred
//     after fix") once RecurredAfterFixAt is set — never just another badge in the same row.
//   - spec observation-shows-linked-obligation: a short link to the linked obligation
//     (observation_obligation edge) once the observation is `promoted`.
// Everything is driven through the REAL production path (RecordObservationFirstSeenAsync /
// RecordObservationRecurrenceAsync / the promoted+Links{observation_obligation} upsert / the
// obligation's own terminal-ok transition auto-firing SyncObservationOnObligationTerminalAsync),
// never a direct data-layer write — exactly what a live extractor + promote + fix + regress cycle
// produces.
//
// live-verification finding (owner, 2026-08-28, screenshot of `observations/live3-prov-check-
// cache-evict` on prod c4f0492): the FIRST version of this test only checked PRESENCE of
// data-testid elements, never their TEXT CONTENT — so it missed two real rendering defects:
// (1) the recurrence count and last-seen date collapsed into one unreadable run ("×32026-08-28
// 19:28:37") with no separator, and (2) the regression banner's "fixed by" link showed the raw
// 32-hex NodeId instead of the project's UI convention (prefer a slug). Both are now covered by
// exact-text assertions (ToHaveTextAsync, not ToContainTextAsync/ToHaveCountAsync) on the count
// element and the fixed-by link, specifically BECAUSE those are the two things a presence check
// cannot catch.
[Collection(nameof(UiCollection))]
public sealed class ObservationUiSignalsTests(WebAppFixture app, ITestOutputHelper output) : IAsyncLifetime
{
	const string Ws = "obs-ui-ws";
	const string Proj = "obs-ui-proj";

	IBrowserContext? _ctx;
	IPage? _page;

	string _recurredId = "";
	string _regressedId = "";
	string _regressedFixerId = "";
	string _promotedId = "";
	string _promotedObligationId = "";

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Observation UI Signals" });

		var tasks = scope.ServiceProvider.GetRequiredService<ITasksService>();

		if (!await tasks.BoardExistsAsync(Proj, SystemBoards.Observations))
			await tasks.CreateBoardAsync(Proj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs ui fixture", null,
				methodologyInstance: TaskBoardMeta.UtilityWorld);
		if (!await tasks.BoardExistsAsync(Proj, "work"))
			await tasks.CreateBoardAsync(Proj, "work", "work", "work fixture", null,
				methodologyInstance: TaskBoardMeta.UtilityWorld);

		var existing = await tasks.GetAsync(Proj, SystemBoards.Observations, includeClosed: true);
		if (existing.Nodes.Count == 0)
		{
			// --- obs-recurred: plain recurrence, no regression, no obligation link ---
			_recurredId = await CreateObservationAsync(tasks, "obs-recurred", "Recurs plainly");
			await tasks.RecordObservationFirstSeenAsync(Proj, _recurredId);
			await tasks.RecordObservationRecurrenceAsync(Proj, _recurredId, currentlyFixed: false);

			// --- obs-regressed: promoted -> fixed (obligation reaches Done) -> recurs while fixed,
			// which both bumps RecurrenceCount AND stamps RecurredAfterFixAt/FixedByNodeId, and
			// reopens the observation fixed -> seen (SyncObservationOnObligationTerminalAsync /
			// RecordObservationRecurrenceAsync's own documented effects — see TasksService.cs). ---
			_regressedId = await CreateObservationAsync(tasks, "obs-regressed", "Regresses after a fix");
			await tasks.RecordObservationFirstSeenAsync(Proj, _regressedId);
			_regressedFixerId = await CreateChoreAsync(tasks, "chore-fixer", "Fix the regressor");
			await PromoteAsync(tasks, "obs-regressed", _regressedId, _regressedFixerId);
			await ResolveChoreAsync(tasks, "chore-fixer", _regressedFixerId);
			await tasks.RecordObservationRecurrenceAsync(Proj, _regressedId, currentlyFixed: true);

			// --- obs-promoted: promoted, obligation still open (never fixed) — just the link. ---
			_promotedId = await CreateObservationAsync(tasks, "obs-promoted", "Promoted, obligation open");
			await tasks.RecordObservationFirstSeenAsync(Proj, _promotedId);
			_promotedObligationId = await CreateChoreAsync(tasks, "chore-obligation", "Address the promoted finding");
			await PromoteAsync(tasks, "obs-promoted", _promotedId, _promotedObligationId);
		}
		else
		{
			_recurredId = existing.Nodes.First(n => n.Key == "obs-recurred").NodeId;
			_regressedId = existing.Nodes.First(n => n.Key == "obs-regressed").NodeId;
			_promotedId = existing.Nodes.First(n => n.Key == "obs-promoted").NodeId;
			var work = await tasks.GetAsync(Proj, "work", includeClosed: true);
			_regressedFixerId = work.Nodes.First(n => n.Key == "chore-fixer").NodeId;
			_promotedObligationId = work.Nodes.First(n => n.Key == "chore-obligation").NodeId;
		}

		_ctx = await app.NewContextAsync(authenticated: true);
		_page = await _ctx.NewPageAsync();
	}

	static async Task<string> CreateObservationAsync(ITasksService tasks, string key, string title)
	{
		var created = await tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch { Key = key, Version = 0, Title = title, Body = "repro details" }]);
		created.Result.Applied.Should().BeTrue();
		return created.Result.Added.Should().ContainSingle().Subject.NodeId;
	}

	static async Task<string> CreateChoreAsync(ITasksService tasks, string key, string title)
	{
		var created = await tasks.UpsertAsync(Proj, "work",
			[new NodePatch { Key = key, Version = 0, Type = "chore", Title = title, Body = "x" }]);
		created.Result.Applied.Should().BeTrue();
		return created.Result.Added.Should().ContainSingle().Subject.NodeId;
	}

	// seen -> promoted, linking the observation_obligation edge in the SAME write (NodePatch.Links
	// — the generic per-kind-slug link door, same one tasks_observation_promote's follow-up
	// relations_create + status write reach for from the MCP side).
	static async Task PromoteAsync(ITasksService tasks, string key, string obsNodeId, string obligationNodeId)
	{
		var v = (await tasks.GetNodeAsync(Proj, obsNodeId))!.Node.Version;
		var promoted = await tasks.UpsertAsync(Proj, SystemBoards.Observations,
			[new NodePatch
			{
				Key = key, Version = v, Status = "promoted",
				Links = new Dictionary<string, IReadOnlyList<string>> { ["observation_obligation"] = [obligationNodeId] },
			}]);
		promoted.Result.Applied.Should().BeTrue();
	}

	// Pending -> InProgress -> Review -> Done (the ONLY path to the work kind's terminal-ok status
	// — Review -> Done is an approval-gated transition, TasksActor(CanApprove: true) is the
	// maintainer door every other approval-gated E2E fixture uses to drive it directly rather than
	// standing up a real approval UI flow this card has nothing to do with).
	static async Task ResolveChoreAsync(ITasksService tasks, string key, string nodeId)
	{
		var v1 = (await tasks.GetNodeAsync(Proj, nodeId))!.Node.Version;
		(await tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = key, Version = v1, Status = "InProgress" }])).Result.Applied.Should().BeTrue();
		var v2 = (await tasks.GetNodeAsync(Proj, nodeId))!.Node.Version;
		(await tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = key, Version = v2, Status = "Review" }])).Result.Applied.Should().BeTrue();
		var v3 = (await tasks.GetNodeAsync(Proj, nodeId))!.Node.Version;
		(await tasks.UpsertAsync(Proj, "work", [new NodePatch { Key = key, Version = v3, Status = "Done" }], new TasksActor(CanApprove: true))).Result.Applied.Should().BeTrue();
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
	public async Task BoardTree_ShowsRecurrenceBadge_RegressionBanner_AndObligationLink_OnTheRightCards()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{SystemBoards.Observations}");

		var recurredCard = _page.Locator($"[data-node-id='{_recurredId}']");
		// Exact text, not ContainText: the live defect was the count and the date fusing into one
		// run ("×32026-08-28…") — an exact match on the COUNT'S OWN element fails if any date text
		// leaked into it, which a mere "contains ×2" check cannot detect.
		await Expect(recurredCard.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");
		await Expect(recurredCard.GetByTestId("node-observation-last-seen-label")).ToContainTextAsync("last seen");
		await Expect(recurredCard.GetByTestId("node-observation-regression")).ToHaveCountAsync(0);
		await Expect(recurredCard.GetByTestId("node-observation-obligation-badge")).ToHaveCountAsync(0);

		var regressedCard = _page.Locator($"[data-node-id='{_regressedId}']");
		await Expect(regressedCard.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");
		await Expect(regressedCard.GetByTestId("node-observation-last-seen-label")).ToContainTextAsync("last seen");
		await Expect(regressedCard.GetByTestId("node-observation-regression")).ToBeVisibleAsync();
		await Expect(regressedCard.GetByTestId("node-observation-regression")).ToContainTextAsync("recurred after fix");
		// The live defect: this used to show the raw 32-hex NodeId. Now the resolved SLUG, both in
		// the link TEXT and in the href route (TaskBoardNodeBySlug, not the opaque NodeId route).
		await Expect(regressedCard.GetByTestId("node-observation-fixed-by-link")).ToHaveTextAsync("fixed by chore-fixer");
		await Expect(regressedCard.GetByTestId("node-observation-fixed-by-link")).Not.ToContainTextAsync(_regressedFixerId);
		await Expect(regressedCard.GetByTestId("node-observation-fixed-by-link")).ToHaveAttributeAsync("href", $"/ui/{Ws}/{Proj}/tasks/work/chore-fixer");

		var promotedCard = _page.Locator($"[data-node-id='{_promotedId}']");
		await Expect(promotedCard.GetByTestId("node-observation-recurrence")).ToHaveCountAsync(0);
		await Expect(promotedCard.GetByTestId("node-observation-regression")).ToHaveCountAsync(0);
	}

	[Fact]
	public async Task BoardTable_ShowsRecurrenceBadge_AndRegressionBanner_OnTheRightRows()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{SystemBoards.Observations}?view=table");

		var recurredRow = _page.Locator($"tr[data-node-id='{_recurredId}']");
		await Expect(recurredRow.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");
		await Expect(recurredRow.GetByTestId("node-observation-last-seen-label")).ToContainTextAsync("last seen");
		await Expect(recurredRow.GetByTestId("node-observation-regression")).ToHaveCountAsync(0);

		var regressedRow = _page.Locator($"tr[data-node-id='{_regressedId}']");
		await Expect(regressedRow.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");
		await Expect(regressedRow.GetByTestId("node-observation-regression")).ToBeVisibleAsync();
		await Expect(regressedRow.GetByTestId("node-observation-fixed-by-link")).ToHaveTextAsync("fixed by chore-fixer");
		await Expect(regressedRow.GetByTestId("node-observation-fixed-by-link")).Not.ToContainTextAsync(_regressedFixerId);
		await Expect(regressedRow.GetByTestId("node-observation-fixed-by-link")).ToHaveAttributeAsync("href", $"/ui/{Ws}/{Proj}/tasks/work/chore-fixer");
	}

	[Fact]
	public async Task NodeDetail_ShowsRecurrenceBadge_RegressionBanner_AndObligationLink()
	{
		// Recurrence + regression, both on one node (obs-regressed is genuinely both: it recurred,
		// and that recurrence happened after a fix).
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/node/{_regressedId}");
		// live-verification finding: exact text on the count's OWN element (not "contains ×2" on
		// the whole badge) — this is the assertion that would have caught the count/date fusing
		// into one unreadable run.
		await Expect(_page.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");
		await Expect(_page.GetByTestId("node-observation-last-seen-label")).ToContainTextAsync("last seen");
		await Expect(_page.GetByTestId("node-observation-regression")).ToBeVisibleAsync();
		await Expect(_page.GetByTestId("node-observation-regression")).ToContainTextAsync("recurred after fix");
		// live-verification finding: the raw 32-hex NodeId used to be the link text; the project's
		// UI convention prefers a slug — asserts the resolved slug appears (both text and href) and
		// the raw NodeId does NOT.
		await Expect(_page.GetByTestId("node-observation-fixed-by-link")).ToHaveTextAsync("fixed by chore-fixer");
		await Expect(_page.GetByTestId("node-observation-fixed-by-link")).Not.ToContainTextAsync(_regressedFixerId);
		await Expect(_page.GetByTestId("node-observation-fixed-by-link")).ToHaveAttributeAsync("href", $"/ui/{Ws}/{Proj}/tasks/work/chore-fixer");
		// Not promoted (reopened to `seen`) — no obligation badge here.
		await Expect(_page.GetByTestId("node-observation-obligation-badge")).ToHaveCountAsync(0);

		// Promoted -> the short obligation link renders next to the status badge, sourced from the
		// SAME exhaustive relations panel this page already renders lower down (data-relation).
		await _page.GotoAsync($"/ui/{Ws}/{Proj}/tasks/node/{_promotedId}");
		await Expect(_page.GetByTestId("node-observation-obligation-badge")).ToBeVisibleAsync();
		// Resolves via the slug-based route (LinkRef found a live board+slug for this target),
		// same as every other resolvable link in the exhaustive relations panel below it.
		await Expect(_page.GetByTestId("node-observation-obligation-link")).ToHaveAttributeAsync("href", $"/ui/{Ws}/{Proj}/tasks/work/chore-obligation");
		// The exhaustive relations panel ALSO carries the same edge (spec
		// observation-shows-linked-obligation's "видно с обеих сторон" claim) — it was already
		// there before this card, this just asserts it stayed true.
		await Expect(_page.Locator("[data-relation^='observation_obligation:']")).ToBeVisibleAsync();
	}
}
