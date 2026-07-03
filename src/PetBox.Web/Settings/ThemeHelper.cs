using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;

namespace PetBox.Web.Settings;

// Maps the user's Theme preference to a daisyUI data-theme value for server-side
// rendering, plus whether the "follow system" inline script must run. daisyUI has no
// "system" theme, so System resolves client-side against prefers-color-scheme.
//
// Server renders "dark" for System/anonymous as a flash-free default; the inline script
// (see Pages/Shared/_ThemeScript.cshtml) then swaps to "light" when the OS prefers light,
// before first paint. Explicit Light/Dark render server-side with NO script.
public static class ThemeHelper
{
	// Resolved data-theme for <html> plus whether the follow-system script should be
	// emitted. `theme` is null for anonymous requests (no stored user setting).
	public static (string DataTheme, bool SystemScript) Resolve(Theme? theme) => theme switch
	{
		Theme.Light => ("light", false),
		Theme.Dark => ("dark", false),
		// System, and the anonymous/no-user default, follow the OS preference client-side.
		_ => ("dark", true),
	};

	// Resolve the effective theme for the current request: the signed-in user's stored
	// UiSettings.Theme, or null (→ follow-system) when anonymous. Shared by the three shell
	// layouts so the resolution block isn't copy-pasted into each.
	public static async Task<(string DataTheme, bool SystemScript)> ResolveForCurrentUserAsync(
		INavigationContext nav, ISettingsResolver settings, HttpContext http)
	{
		Theme? theme = null;
		var userId = nav.IsAuthenticated ? http.User.FindFirst(PetBoxClaims.UserId)?.Value : null;
		if (!string.IsNullOrEmpty(userId))
		{
			var ui = await settings.GetAsync<UiSettings>(Scope.User, userId);
			theme = ui.Theme;
		}
		return Resolve(theme);
	}
}
