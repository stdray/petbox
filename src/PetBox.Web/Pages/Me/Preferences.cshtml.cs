using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Settings;

namespace PetBox.Web.Pages.Me;

[Authorize]
// Reads and writes at Scope.User keyed by the caller's OWN user-id claim (TryGetUserId below): no
// project or workspace is named by the request or stored with the row. The same reading, and the same
// class, as the /api/ui/board-filter-prefs endpoint the REST wave declared — the two are the
// per-user-preference surface of the same setting store and must not answer this question differently.
[TenantExempt(TenantExemption.Identity,
	"a per-user preference read and written at Scope.User under the caller's own user id; no project "
	+ "or workspace is named by the request or stored with the row")]
public sealed class PreferencesModel : PageModel
{
	readonly ISettingsResolver _resolver;

	public PreferencesModel(ISettingsResolver resolver) => _resolver = resolver;

	// BrowserState, not the retired UiSettings (work `ui-state-theme-unify`): Theme is now a
	// [Setting] property on the SAME combined record IUiState resolves everywhere else.
	// SettingsFormFieldSelector only surfaces [Setting]-tagged properties (Theme), never the
	// [BrowserState]-tagged cookie ones (SidebarPinned), so this page still shows exactly one field.
	public BrowserState Current { get; private set; } = new();
	public string UserIdString { get; private set; } = string.Empty;
	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!TryGetUserId(out var userId, out var userIdString))
			return RedirectToPage("/Login");

		UserIdString = userIdString;
		Current = await _resolver.GetAsync<BrowserState>(Scope.User, userIdString);
		return Page();
	}

	public async Task<IActionResult> OnPostSaveAsync(Theme Theme)
	{
		if (!TryGetUserId(out var userId, out var userIdString))
			return RedirectToPage("/Login");

		UserIdString = userIdString;
		var old = await _resolver.GetAsync<BrowserState>(Scope.User, userIdString);
		var updated = old with { Theme = Theme };

		await _resolver.SetAsync(Scope.User, userIdString, updated, old, userId);

		Current = updated;
		SuccessMessage = "Preferences saved.";
		return Page();
	}

	bool TryGetUserId(out long userId, out string userIdString)
	{
		var raw = User.FindFirst(PetBoxClaims.UserId)?.Value;
		if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
		{
			userIdString = userId.ToString(CultureInfo.InvariantCulture);
			return true;
		}
		userIdString = string.Empty;
		return false;
	}
}
