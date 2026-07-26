using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Admin;

// Per-project log retention used to live in a bespoke control on the project Info page (/info,
// card ui-log-retention-settings-fix); admin-routes-and-pages item 3 moved it to the generic
// project Settings page (/settings) — LogSettings.RetentionDays is already in
// SettingsScopePolicy.Records, so it renders there via the same engine as every other cascading
// setting, instead of a one-off hint UI Info had to maintain. This page is kept only as a redirect
// so existing links/bookmarks to /log land on the real retention control instead of a 404 or an
// empty form.
[Authorize(Policy = "WorkspaceAdmin")]
// {projectKey} in the route IS the target tenant; ProjectWorkspaceBindingFilter still binds it to
// {workspaceKey} as a ROUTING question (404 on a mismatched URL), which is a different question.
[TenantFrom(TenantSource.Route, "projectKey")]
public sealed class ProjectLogSettingsModel : PageModel
{
	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public IActionResult OnGet() => Redirect(Routes.ProjectSettingsAdmin(WorkspaceKey, ProjectKey));
}
