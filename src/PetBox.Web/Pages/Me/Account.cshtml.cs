using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Me;

[Authorize]
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
