using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Me;

[Authorize]
// The whole /ui/me/* zone is the caller's own account: no tenant in the route, and every value on
// the page is read off the authenticated principal itself. Here that is literally all it does —
// username, user id and the sysadmin flag, straight from the claims.
[TenantExempt(TenantExemption.Identity,
	"shows the caller their own username, user id and sysadmin flag, read off their own claims")]
public sealed class AccountModel : PageModel
{
	public string Username { get; private set; } = string.Empty;
	public long UserId { get; private set; }
	public bool IsSysAdmin { get; private set; }

	public void OnGet()
	{
		Username = User.Identity?.Name ?? string.Empty;
		var userIdRaw = User.FindFirst(PetBox.Core.Auth.PetBoxClaims.UserId)?.Value;
		if (long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
			UserId = id;
		IsSysAdmin = User.FindFirst(PetBox.Core.Auth.PetBoxClaims.IsSysAdmin)?.Value == "true";
	}
}
