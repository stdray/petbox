using PetBox.Core.Settings;

namespace PetBox.Web.Settings;

// Pure presentation mapping ONLY: BrowserState.Theme (resolved by PetBox.Web.Settings.IUiState —
// the single UI-state mechanism, see BrowserState.cs) to a daisyUI data-theme value plus whether
// the "follow system" inline script (_ThemeScript.cshtml) must run. daisyUI has no "system" theme,
// so System resolves client-side against prefers-color-scheme.
//
// Before work `ui-state-theme-unify` this type ALSO reached the DB itself
// (ResolveForCurrentUserAsync, since removed) — a second, parallel resolution path alongside
// UiStateResolver/IUiState for everything else. It no longer does: callers resolve BrowserState via
// IUiState first (same as any other BrowserState property) and hand the already-resolved Theme in
// here. This is intentionally the ONLY method left — there is nothing else for ThemeHelper to do.
public static class ThemeHelper
{
	// Server renders "dark" for System as a flash-free default; the inline script (see
	// Pages/Shared/_ThemeScript.cshtml) then swaps to "light" when the OS prefers light, before
	// first paint. Explicit Light/Dark render server-side with NO script.
	public static (string DataTheme, bool SystemScript) Resolve(Theme theme) => theme switch
	{
		Theme.Light => ("light", false),
		Theme.Dark => ("dark", false),
		// System follows the OS preference client-side, before paint.
		_ => ("dark", true),
	};
}
