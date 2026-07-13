using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Admin;

// Per-project log retention used to live in a bespoke control on the project Info page (/info,
// card ui-log-retention-settings-fix); admin-routes-and-pages item 3 moved it to the generic
// project Settings page (/settings) — LogSettings.RetentionDays is already in
// SettingsScopePolicy.Records, so it renders there via the same engine as every other cascading
// setting, instead of a one-off hint UI Info had to maintain. This page is kept only as a redirect
// so existing links/bookmarks to /log land on the real retention control instead of a 404 or an
// empty form.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectLogSettingsModel : PageModel
{
	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public IActionResult OnGet() => Redirect(Routes.ProjectSettingsAdmin(WorkspaceKey, ProjectKey));
}
