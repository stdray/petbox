using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Auth;
using YobaBox.Core.Settings;
using YobaBox.Web.Navigation;

namespace YobaBox.Web.Pages;

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
		var userIdRaw = User.FindFirst(YobaBoxClaims.UserId)?.Value;

		if (long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			var ui = await _settings.GetAsync<UiSettings>(Scope.User, userIdRaw!);
			return ui.DefaultHome switch
			{
				DefaultHome.AllLogs => Redirect(Routes.WorkspaceLogs(ws)),
				// LastProject support pends MembershipSettings (next migration);
				// fall back to Status until then.
				DefaultHome.LastProject => Redirect(Routes.Workspace(ws)),
				_ => Redirect(Routes.Workspace(ws)),
			};
		}

		return Redirect(Routes.Workspace(ws));
	}
}
