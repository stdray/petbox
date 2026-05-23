using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YobaBox.Web.Pages;

[Authorize]
public sealed class IndexModel : PageModel
{
	public void OnGet() { }
}
