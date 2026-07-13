namespace PetBox.Core.Settings;

// The single per-request UI-state record resolved before the layout renders (see
// PetBox.Web.Settings.UiStateResolver, and _Layout.cshtml where it's resolved next to
// ThemeHelper's data-theme). It mixes BOTH storage branches in one type:
//   - a property marked [Setting] (Scope.User) resolves from the DB — cross-device preferences.
//   - a property marked [BrowserState] resolves from the single `petbox.ui` JSON cookie —
//     window/device state, visible to the server before HTML is sent.
//
// SidebarPinned is the first real consumer (work `sidebar-pin-server-state`): whether the
// sidebar drawer is docked open (pinned) or a floating collapsible overlay. Window/device state,
// not a cross-device preference, so it lives in the cookie branch. Four more follow-ups — board
// view mode, board filters, kql panel pin, the dead sidebar-tree cookie — each add THEIR own
// property here (tagged [Setting] or [BrowserState] per the storage-boundary call in their own
// spec) instead of standing up a parallel resolver or an extra cookie.
public sealed record BrowserState
{
	// Docked-open drawer (true) vs a floating collapsible overlay (false). Default true matches
	// the pre-existing hardcoded `drawer-open` every layout used to always print, so an
	// anonymous/first-time visitor (no cookie yet) still sees the drawer open, unchanged.
	[BrowserState(Key = "sidebarPinned")]
	public bool SidebarPinned { get; init; } = true;
}
