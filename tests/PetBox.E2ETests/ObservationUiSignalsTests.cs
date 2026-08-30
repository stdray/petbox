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

	// board-view-fields / board-view-cross-device: field selection is a per-(project,board) DB
	// preference shared by EVERY test that visits it (WebAppFixture's `authenticated: true`
	// contexts all carry the SAME logged-in user — see BoardViewCrossDeviceTests' own header
	// comment). A test that submits `fieldsSet=1` therefore PERSISTS its choice for every other
	// test on the SAME board, including the default-rendering assertions above. This second,
	// otherwise-untouched project (same pattern BoardViewCrossDeviceTests uses: its own board) is
	// where the field-toggle tests below write, so they can never leak into `_recurredId`'s board.
	const string FieldsProj = "obs-ui-fields-proj";

	IBrowserContext? _ctx;
	IPage? _page;

	string _recurredId = "";
	string _regressedId = "";
	string _regressedFixerId = "";
	string _promotedId = "";
	string _promotedObligationId = "";
	string _fieldsFixtureId = "";

	public async ValueTask InitializeAsync()
	{
		using var scope = app.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		if (!await db.Workspaces.AnyAsync(w => w.Key == Ws))
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		if (!await db.Projects.AnyAsync(p => p.Key == Proj))
			await db.InsertAsync(new Project { Key = Proj, WorkspaceKey = Ws, Name = "Observation UI Signals" });
		if (!await db.Projects.AnyAsync(p => p.Key == FieldsProj))
			await db.InsertAsync(new Project { Key = FieldsProj, WorkspaceKey = Ws, Name = "Observation UI Signals — field toggles" });

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

		if (!await tasks.BoardExistsAsync(FieldsProj, SystemBoards.Observations))
			await tasks.CreateBoardAsync(FieldsProj, SystemBoards.Observations, SystemBoards.ObservationKind, "obs ui field-toggle fixture", null,
				methodologyInstance: TaskBoardMeta.UtilityWorld);
		var existingFields = await tasks.GetAsync(FieldsProj, SystemBoards.Observations, includeClosed: true);
		if (existingFields.Nodes.Count == 0)
		{
			// One node, recurred once (RecurrenceCount == 2, so ×2 is checkable), never given a
			// sessionId — the natural "no session recorded" fixture the sessions-empty-state test
			// needs. Never touched by fieldsSet=1 writes on ANY other board — see FieldsProj's own
			// header comment for why it's a separate project.
			var created = await tasks.UpsertAsync(FieldsProj, SystemBoards.Observations,
				[new NodePatch { Key = "obs-fields-fixture", Version = 0, Title = "Field toggle fixture", Body = "x" }]);
			created.Result.Applied.Should().BeTrue();
			_fieldsFixtureId = created.Result.Added.Should().ContainSingle().Subject.NodeId;
			await tasks.RecordObservationFirstSeenAsync(FieldsProj, _fieldsFixtureId);
			await tasks.RecordObservationRecurrenceAsync(FieldsProj, _fieldsFixtureId, currentlyFixed: false);
		}
		else
		{
			_fieldsFixtureId = existingFields.Nodes.First(n => n.Key == "obs-fields-fixture").NodeId;
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
		// recurrence-and-session-provenance-as-board-fields: obs-promoted was only ever
		// RecordObservationFirstSeenAsync'd — RecurrenceCount == 1, a first-ever sighting that
		// NEVER recurred. Before this task's fix, the badge partial gated on `RecurrenceCount > 1`
		// and hid entirely here — exactly the reported defect (the owner's card showed nothing at
		// count 1, indistinguishable from a broken mechanism). It must now show ×1.
		await Expect(promotedCard.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×1");
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

		// recurrence-and-session-provenance-as-board-fields: same first-sighting fix as the tree
		// test above, checked in table too — a per-view fix that only one view's test exercised is
		// exactly the gap the card calls out.
		var promotedRow = _page.Locator($"tr[data-node-id='{_promotedId}']");
		await Expect(promotedRow.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×1");
	}

	// recurrence-and-session-provenance-as-board-fields: kanban and outline never called the
	// recurrence badge partial AT ALL before this task (unlike tree/table, which already rendered
	// it but gated it on the now-removed RecurrenceCount>1 threshold) — a live gap the card
	// explicitly calls out ("именно проверка одного вида пропустила дыру в kanban/outline в
	// прошлый раз"). Both view modes reuse the SAME data-testid contract as tree/table, so the
	// same locator pattern proves the wiring, not just the threshold removal.
	[Fact]
	public async Task BoardKanban_ShowsRecurrenceBadge_OnTheRightCards()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{SystemBoards.Observations}?view=kanban");

		var recurredCard = _page.Locator($"[data-node-id='{_recurredId}']");
		await Expect(recurredCard.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");

		var promotedCard = _page.Locator($"[data-node-id='{_promotedId}']");
		await Expect(promotedCard.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×1");
	}

	[Fact]
	public async Task BoardOutline_ShowsRecurrenceBadge_OnTheRightRows()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/{SystemBoards.Observations}?view=outline");

		var recurredRow = _page.Locator($"[data-node-id='{_recurredId}']");
		await Expect(recurredRow.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×2");

		var promotedRow = _page.Locator($"[data-node-id='{_promotedId}']");
		await Expect(promotedRow.GetByTestId("node-observation-recurrence-count")).ToHaveTextAsync("×1");
	}

	// board-view-fields: Recurrence is a togglable field like any other — turning it OFF must
	// actually hide the badge, not just leave it defaulted on. `fields=slug&fieldsSet=1` is a
	// deliberately empty-of-recurrence explicit selection (fieldsSet=1 disambiguates it from "no
	// fields param at all", which would fall through to the default-on behaviour every other test
	// here relies on).
	// Runs on FieldsProj (a SEPARATE project/board — see FieldsProj's own header comment): the
	// per-(project,board) preference this `fieldsSet=1` write persists is SHARED by every test that
	// later visits the same board with no explicit `fields=` of its own (every default-rendering
	// test above), and every authenticated E2E context is the SAME logged-in user (WebAppFixture),
	// so writing it on the `_recurredId`/`_promotedId` board would silently turn Recurrence off for
	// them too — an isolation bug this class had before this fixture existed (found by actually
	// running the suite, not read off the code).
	[Fact]
	public async Task BoardTable_RecurrenceField_HiddenWhenExplicitlyTurnedOff()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{FieldsProj}/tasks/{SystemBoards.Observations}?view=table&fields=slug&fieldsSet=1");

		var row = _page.Locator($"tr[data-node-id='{_fieldsFixtureId}']");
		await Expect(row.GetByTestId("node-observation-recurrence")).ToHaveCountAsync(0);
	}

	// spec node-session-provenance-visible-in-ui: Sessions is opt-in (off by default) on every
	// board kind, unlike Recurrence. Turning it on here exercises BOTH halves of the spec: a node
	// with no recorded session renders the explicit "no session recorded" text (not nothing — the
	// fixtures in this suite never pass a sessionId to UpsertAsync, so every node here genuinely
	// has none), and the field being OFF (a plain `?view=table` with no saved preference yet)
	// renders nothing at all — the two states stay visibly different. Also on FieldsProj, for the
	// same isolation reason as the test above — and run in THIS order (off, then on) within one
	// test so the "off" half is asserted before this test's OWN write happens.
	[Fact]
	public async Task BoardTable_SessionsField_OffByDefault_ExplicitEmptyStateWhenOn()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{FieldsProj}/tasks/{SystemBoards.Observations}?view=table");
		var rowFieldOff = _page.Locator($"tr[data-node-id='{_fieldsFixtureId}']");
		await Expect(rowFieldOff.GetByTestId("node-session-provenance")).ToHaveCountAsync(0);

		await _page.GotoAsync($"/ui/{Ws}/{FieldsProj}/tasks/{SystemBoards.Observations}?view=table&fields=slug&fields=sessions&fieldsSet=1");
		var row = _page.Locator($"tr[data-node-id='{_fieldsFixtureId}']");
		await Expect(row.GetByTestId("node-session-provenance-empty")).ToHaveTextAsync("no session recorded");
	}

	// spec node-session-provenance-visible-in-ui: the node detail page has no fields dialog at all
	// (board-view-fields is a board-view affordance) — this partial renders UNCONDITIONALLY there,
	// so the empty state must be visible with no query param needed.
	[Fact]
	public async Task NodeDetail_SessionProvenance_ShowsExplicitEmptyState()
	{
		await _page!.GotoAsync($"/ui/{Ws}/{Proj}/tasks/node/{_recurredId}");
		await Expect(_page.GetByTestId("node-session-provenance-empty")).ToHaveTextAsync("no session recorded");
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
