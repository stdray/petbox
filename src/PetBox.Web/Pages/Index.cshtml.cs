using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Settings;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly INavigationContext _nav;
	readonly ISettingsResolver _settings;

	public IndexModel(INavigationContext nav, ISettingsResolver settings)
	{
		_nav = nav;
		_settings = settings;
	}

	public async Task<IActionResult> OnGetAsync()
	{
		var ws = _nav.CurrentWorkspaceKey;
		var userIdRaw = User.FindFirst(PetBoxClaims.UserId)?.Value;

		if (long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			var ui = await _settings.GetAsync<UiSettings>(Scope.User, userIdRaw!);
			return ui.DefaultHome switch
			{
				// "Logs (all)" was removed (no cross-project merge); AllLogs now
				// resolves to the workspace status page like the other options.
				// LastProject support pends MembershipSettings (next migration).
				_ => Redirect(Routes.Workspace(ws)),
			};
		}

		return Redirect(Routes.Workspace(ws));
	}
}
