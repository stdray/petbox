namespace PetBox.Core.Settings;

// board-view-cross-device: which mode (+ tag-grouping `by`) a (project,board) pair renders in.
// Cross-device by explicit maintainer request ("board settings should work on all his devices"),
// so it lives in the DB branch, keyed per user — NOT per (project,board) rows (Scope has no
// per-board axis, and adding one is a much bigger change than the realistic cardinality justifies:
// a single user's total board count across every project is realistically dozens, not thousands),
// but as ONE JSON dictionary value on ONE [Setting] property, keyed by "projectKey/board" (the
// same composite key ts/board.ts's retired localStorage keys already used). Fields (board-view-
// fields, board-view-display-config-impl's "same mechanism as view mode, no third one" — see that
// work node's body point 5) rides along in the SAME per-board record rather than a second
// dictionary, since both are "what does THIS board look like" and both write on the exact same
// trigger (an explicit `?view=`/`?fields=` navigation).
public sealed record BoardViewPreference
{
	public string? Mode { get; init; }
	public string? By { get; init; }
	// Board-view-fields' enabled-key set, BoardFieldConfig.ToCsv() shape (comma-joined keys, canonical
	// BoardFieldNames.Options order) — a plain string, not a nested object, so this record stays
	// JSON-trivial to encode/decode through SettingsResolver's generic "json" fallback branch.
	public string? Fields { get; init; }
}

// board-view-cross-device / board-filters-server-state: per-user task-board preferences, resolved
// through the SAME generic [Setting]/ISettingsResolver mechanism as every other Setting record
// (LogSettings, DashboardSettings, RepoSettings, ...) — deliberately NOT folded into BrowserState.
//
// Why a separate record: BrowserState is the ONE combined record `IUiState`/every layout resolves
// on EVERY page before render (see BrowserState.cs). Folding board preferences in there would:
//   1. Leak them into the generic settings form. SettingsFormFieldSelector.GetEditable walks EVERY
//      [Setting]-tagged property of whatever record type a settings page names, with no per-
//      property "hide from form" escape hatch — a Dictionary property (ViewPreferences) has no
//      form renderer at all, and the scalar ones have no business appearing on /ui/me/preferences
//      next to Theme. This was caught by SettingsFormFieldSelectorTests.
//      BrowserState_AtUserScope_ShowsTheme_TheOnlyLive_SettingsForm_Caller going red the first time
//      these lived on BrowserState.
//   2. Pay a DB resolve for a growing per-board dictionary on every non-board page's
//      IUiState.GetAsync() call, for state only TaskBoardModel ever reads.
// "Exactly one mechanism" (the maintainer's actual invariant, enforced by
// UiStateSingleMechanismGuardTests) is about not re-inventing raw CLIENT storage
// (localStorage/sessionStorage/document.cookie) outside ts/ui-state.ts — it was never a rule that
// every DB [Setting] lives on one C# record; the codebase already has half a dozen distinct
// [Setting] record types (LogSettings, DashboardSettings, RepoSettings, ...) resolved the exact
// same way. TaskBoardModel resolves THIS record directly via ISettingsResolver.GetAsync/SetAsync —
// no IUiState involvement, since board pages don't need Theme/SidebarPinned/KqlPanelPinned at all.
public sealed record BoardPreferences
{
	[Setting(TopLevel = Scope.User, Key = "board.viewPrefs",
		Description = "Per-board saved view mode, tag-grouping dimension, and field selection.")]
	public Dictionary<string, BoardViewPreference> ViewPreferences { get; init; } = new(StringComparer.Ordinal);

	// board-filters-server-state: active-only / sort are GLOBAL preferences (board-independent),
	// confirmed still correct — unlike view mode/fields, hiding closed items or picking a sort key
	// is a personal habit that applies the same way regardless of which board you're looking at,
	// not a per-board display choice.
	[Setting(TopLevel = Scope.User, Key = "board.activeOnly",
		Description = "Hide closed (terminal-status) plan nodes on task boards by default.")]
	public bool ActiveOnly { get; init; } = true;

	// BoardSortKeys.All ("priority"|"created"|"updated"|"title") — an unrecognized/stale value (a
	// removed sort key) is tolerated the same way BoardViewModeRegistry tolerates an unknown view
	// name: TaskBoardModel's comparator falls back to "priority" rather than throwing.
	[Setting(TopLevel = Scope.User, Key = "board.sortBy", Description = "Task board sort key.")]
	public string SortBy { get; init; } = "priority";

	[Setting(TopLevel = Scope.User, Key = "board.sortDesc", Description = "Task board sort direction.")]
	public bool SortDesc { get; init; } = false;
}
