namespace PetBox.Core.Settings;

// Dark/Light/System are the ORIGINAL members — never renamed or reordered (values persist in the
// DB as strings via JsonStringEnumConverter; existing user rows must keep resolving). Nord and
// Retro (work `ui-theme-palette-expand`) are the maintainer's two non-white light-theme
// candidates to trial side by side — appended, not inserted, for the same reason. Whichever loses
// gets dropped later; do not remove either without the maintainer's call.
public enum Theme { Dark, Light, System, Nord, Retro }

// The single per-request UI-state record resolved before the layout renders (see
// PetBox.Web.Settings.UiStateResolver, and _Layout.cshtml where it's resolved before RenderBody).
// It mixes BOTH storage branches in one type:
//   - a property marked [Setting] (Scope.User) resolves from the DB — cross-device preferences.
//   - a property marked [BrowserState] resolves from the single `petbox.ui` JSON cookie —
//     window/device state, visible to the server before HTML is sent.
//
// Theme (work `ui-state-theme-unify`) folds in what used to be a SECOND, parallel mechanism: a
// standalone `UiSettings` record + a `ThemeHelper.ResolveForCurrentUserAsync` that duplicated the
// DB-branch resolution `UiStateResolver`/`ISettingsResolver` already do generically. Same TopLevel
// (Scope.User) and same Key (`ui.theme`) as the retired `UiSettings.Theme` — existing rows in the
// Settings table keep resolving unchanged. `ThemeHelper` is now a pure presentation mapping
// (Theme -> daisyUI data-theme + whether the follow-system script runs), with no resolution path
// of its own; PetBox.Web.Settings.IUiState is the ONLY place that reaches the DB or the cookie.
//
// SidebarPinned (work `sidebar-pin-server-state`): whether the sidebar drawer is docked open
// (pinned) or a floating collapsible overlay. Window/device state, not a cross-device preference,
// so it lives in the cookie branch.
//
// KqlPanelPinned (work `kql-panel-pin-server-state`): same disease as SidebarPinned had — the KQL
// search panel's sticky/shadow-lg classes used to be applied by ts/logs.ts from
// localStorage['petbox.kqlPanelPinned'] AFTER paint. Same cure, same storage branch.
//
// The dead sidebar-tree cookie (`petbox.sidebar.tree`, work `sidebar-tree-cookie-dead`) was
// DELETED rather than migrated here — see the design-decision comment on that work node for why
// (no markup ever carried the `data-tree-key` the mechanism needed, and the two real disclosure
// trees in the sidebar — the lazy htmx Logs/Databases nodes — can't be pre-opened without also
// eagerly rendering their lazy content, which is a materially bigger feature than "wire up a
// cookie read").
//
// Remaining follow-ups — board view mode, board filters — each add THEIR own property here
// (tagged [Setting] or [BrowserState] per the storage-boundary call in their own spec) instead of
// standing up a parallel resolver or an extra cookie.
public sealed record BrowserState
{
	// DB branch, Scope.User, cross-device. Default is Theme.System — deliberately not Theme.Dark
	// (the OLD UiSettings.Theme record default): the old ThemeHelper special-cased a TRUE anonymous
	// request (no userId at all) to a null theme that its own Resolve() mapped to the
	// follow-system branch, while an AUTHENTICATED user with no stored row got the record's literal
	// Dark default via the same resolver. Once there is one resolver, not two, that special case
	// can't be kept without re-introducing a second theme resolution path — so both "no user id"
	// (new UiStateResolver.ResolveAsync short-circuits to `new T()`) and "authenticated, never
	// touched preferences" (ISettingsResolver.GetAsync also starts from `new T()` when no row
	// matches) now land on the SAME default, Theme.System, and get the SAME (dark, follow-system
	// script) rendering the old anonymous path always had. No exception, no regression for
	// anonymous visitors; a first-time authenticated user now also follows the OS preference
	// instead of being hardcoded dark, which is a deliberate side effect of unifying the mechanism,
	// not an accident.
	[Setting(TopLevel = Scope.User, Key = "ui.theme", Description = "Color theme for the UI.")]
	public Theme Theme { get; init; } = Theme.System;

	// Docked-open drawer (true) vs a floating collapsible overlay (false). Default true matches
	// the pre-existing hardcoded `drawer-open` every layout used to always print, so an
	// anonymous/first-time visitor (no cookie yet) still sees the drawer open, unchanged.
	[BrowserState(Key = "sidebarPinned")]
	public bool SidebarPinned { get; init; } = true;

	// Sticky/pinned KQL search panel on the Logs page (stays visible while scrolling the event
	// list). Default false matches the pre-existing hardcoded `aria-pressed="false"` every Logs
	// page used to always print, so a first-time visitor (no cookie yet) sees the same unpinned
	// panel as before.
	[BrowserState(Key = "kqlPanelPinned")]
	public bool KqlPanelPinned { get; init; } = false;
}
