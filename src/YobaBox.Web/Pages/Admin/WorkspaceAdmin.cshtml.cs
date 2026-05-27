using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace YobaBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceMember")]
public sealed class WorkspaceAdminModel : PageModel
{
	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	public IActionResult OnGet() => Redirect(Routes.WorkspaceAdminMembers(WorkspaceKey));
}
