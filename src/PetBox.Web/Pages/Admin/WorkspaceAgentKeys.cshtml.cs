using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// workspace-admin-owns-keys: the workspace admin already MINTS keys for their projects
// (ProjectConnect, WorkspaceAdmin policy) but could neither see nor revoke them — list+revoke
// lived only on the sysadmin page. A leaked key left the admin of the affected workspace
// powerless over their own project until a sysadmin showed up. This page closes that asymmetry:
// the same policy that mints can now list and revoke — but ONLY the keys of projects in THIS
// workspace.
//
// The confinement is enforced in AgentKeyAdminService, inside the DELETE — not by filtering the
// rendered list. Revoke is addressed by the key VALUE, so a forged POST naming another tenant's
// key would otherwise kill it while every rendered page looked correct (the exact IDOR shape of
// workspace-access-isolation). ProjectWorkspaceBindingFilter does not help here: it binds
// {projectKey} to {workspaceKey}, and this route carries only {workspaceKey}.
//
// WorkspaceKey is bound from the ROUTE only ([FromRoute], never a plain property) — a form-bound
// workspace key would let the POST body choose its own scope and hand back the whole hole
// (authz-bypass-project-create; same reason ProjectConnect binds this way).
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class WorkspaceAgentKeysModel(AgentKeyAdminService keys) : PageModel
{
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public IReadOnlyList<AgentKeyRow> Keys { get; private set; } = [];

	public async Task OnGetAsync() =>
		Keys = await keys.ListAsync(WorkspaceKey, HttpContext.RequestAborted);

	public async Task<IActionResult> OnPostRevokeAsync(string key)
	{
		// 404, not 403: a workspace admin must not be able to tell "not yours" from "does not
		// exist" — either answer would let them enumerate another tenant's keys.
		if (!await keys.RevokeAsync(key, WorkspaceKey, HttpContext.RequestAborted))
			return NotFound();

		this.NotifySuccess("API key revoked.");
		return RedirectToPage(new { workspaceKey = WorkspaceKey });
	}
}
