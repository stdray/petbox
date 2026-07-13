using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// Sysadmin management view for DB-minted API keys across ALL projects: lists them (expiring
// and permanent) and revokes. Minting moved off this page — per-project keys are minted from
// a project's Connect page; cross-project / TTL keys via the MCP apikey_create tool.
//
// The workspace-scoped twin is WorkspaceAgentKeysModel (WorkspaceAdmin, only this workspace's
// keys). Both share AgentKeyAdminService — the null workspace here IS "every key", the sysadmin's
// deliberate free pass.
[Authorize(Policy = "SysAdmin")]
public sealed class AgentKeysModel(AgentKeyAdminService keys) : PageModel
{
	public IReadOnlyList<AgentKeyRow> Keys { get; private set; } = [];

	public async Task OnGetAsync() =>
		Keys = await keys.ListAsync(workspaceKey: null, HttpContext.RequestAborted);

	public async Task<IActionResult> OnPostRevokeAsync(string key)
	{
		if (!await keys.RevokeAsync(key, workspaceKey: null, HttpContext.RequestAborted))
			return NotFound();

		this.NotifySuccess("API key revoked.");
		return RedirectToPage();
	}

	// Rename / re-scope / set+clear the default project (spec apikey-mutable). The sysadmin's scope is
	// the null workspace — every key, anywhere — but the outcome handling is identical to the
	// workspace page's, because both go through the same service.
	public async Task<IActionResult> OnPostEditAsync(string key, string name, string[] scopes, string? defaultProject)
	{
		var edit = new AgentKeyEdit(key, name, scopes ?? [], defaultProject);
		switch (await keys.UpdateAsync(edit, workspaceKey: null, HttpContext.RequestAborted))
		{
			case KeyUpdateResult.Updated:
				this.NotifySuccess("API key updated.");
				return RedirectToPage();
			case KeyUpdateResult.Refused refused:
				// The refusal is shown, never swallowed — a redirect with nothing to say is exactly the
				// silent form failure this feature exists to eliminate.
				this.NotifyError(refused.Reason);
				return RedirectToPage();
			default:
				return NotFound();
		}
	}
}
