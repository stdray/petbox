using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Admin;

// Per-project log retention now lives ONLY on the project Info page (/info), which owns the
// single working override control (card ui-log-retention-settings-fix). At project scope this
// page had no configurable fields — every LogSettings property caps out at Workspace/System
// scope, so the form rendered empty yet still reported a false "Log settings saved." on a no-op
// and re-POSTed on refresh (no PRG). The page is kept only as a redirect so existing
// links/bookmarks to /log land on the real retention control instead of an empty form.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectLogSettingsModel : PageModel
{
	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public IActionResult OnGet() => Redirect(Routes.ProjectSettings(WorkspaceKey, ProjectKey));
}
