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

	// The app root always lands on the current workspace status page.
	public IActionResult OnGet() => Redirect(Routes.Workspace(_nav.CurrentWorkspaceKey));
}
