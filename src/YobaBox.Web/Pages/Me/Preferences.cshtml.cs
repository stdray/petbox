using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Auth;
using YobaBox.Core.Settings;
using YobaBox.Web.Settings;

namespace YobaBox.Web.Pages.Me;

[Authorize]
public sealed class PreferencesModel : PageModel
{
	readonly ISettingsResolver _resolver;

	public PreferencesModel(ISettingsResolver resolver) => _resolver = resolver;

	public UiSettings Current { get; private set; } = new();
	public string UserIdString { get; private set; } = string.Empty;
	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public async Task<IActionResult> OnGetAsync()
	{
		if (!TryGetUserId(out var userId, out var userIdString))
			return RedirectToPage("/Login");

		UserIdString = userIdString;
		Current = await _resolver.GetAsync<UiSettings>(Scope.User, userIdString);
		return Page();
	}

	public async Task<IActionResult> OnPostSaveAsync(Theme Theme, DefaultHome DefaultHome)
	{
		if (!TryGetUserId(out var userId, out var userIdString))
			return RedirectToPage("/Login");

		UserIdString = userIdString;
		var old = await _resolver.GetAsync<UiSettings>(Scope.User, userIdString);
		var updated = old with { Theme = Theme, DefaultHome = DefaultHome };

		await _resolver.SetAsync(Scope.User, userIdString, updated, old, userId);

		Current = updated;
		SuccessMessage = "Preferences saved.";
		return Page();
	}

	bool TryGetUserId(out long userId, out string userIdString)
	{
		var raw = User.FindFirst(YobaBoxClaims.UserId)?.Value;
		if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
		{
			userIdString = userId.ToString(CultureInfo.InvariantCulture);
			return true;
		}
		userIdString = string.Empty;
		return false;
	}
}
