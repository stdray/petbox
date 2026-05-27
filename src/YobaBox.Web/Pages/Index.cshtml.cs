using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Web.Navigation;

namespace YobaBox.Web.Pages;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly INavigationContext _nav;

	public IndexModel(INavigationContext nav) => _nav = nav;

	public IActionResult OnGet() => Redirect(Routes.Workspace(_nav.CurrentWorkspaceKey));
}
