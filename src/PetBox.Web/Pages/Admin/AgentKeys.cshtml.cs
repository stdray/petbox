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
}
