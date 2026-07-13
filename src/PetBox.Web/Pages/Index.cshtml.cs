using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly INavigationContext _nav;

	public IndexModel(INavigationContext nav) => _nav = nav;

	public string? Username { get; private set; }

	// The app root lands on the current workspace status page — unless the user has no workspace
	// at all, in which case there is nowhere to land: the old code redirected to the "$system"
	// fallback (a workspace a fresh Regular account is not a member of), which now 403s. Render
	// the empty state instead of bouncing the user between a redirect and an access-denied page.
	public IActionResult OnGet()
	{
		Username = _nav.Username;
		return _nav.CurrentWorkspaceKey is { } ws
			? Redirect(Routes.Workspace(ws))
			: Page();
	}
}
