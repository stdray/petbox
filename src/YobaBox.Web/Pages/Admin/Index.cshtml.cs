using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class IndexModel : PageModel
{
	public void OnGet() { }
}
