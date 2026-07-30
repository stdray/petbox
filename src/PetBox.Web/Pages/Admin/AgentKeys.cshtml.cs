using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
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
// `provisioning`, the same class the apikey_* MCP tools carry, and for the identical reason: this page
// lists, revokes and RE-SCOPES keys across every project — `workspaceKey: null` in every call below IS
// "every key, anywhere". A key-editing verb cannot be confined to the tenant it edits for.
//
// The class is deliberately not `fleet-wide`: what crosses the boundary here is not a node, it is
// another tenant's credential, and the spec's note that `admin:provision` is de facto root over every
// tenant is the fact this declaration makes visible rather than hides. Narrowing it is the separate
// idea that note points at, not a line in this wave.
//
// The workspace-scoped twin (Admin/WorkspaceAgentKeys, `workspaceKey` from the route) is NOT exempt and
// declares its tenant — same service, one tenant, so the two pages differ exactly where they should.
[TenantExempt(TenantExemption.Provisioning,
	"lists, revokes and re-scopes DB-minted api keys ACROSS all projects (workspaceKey: null is 'every "
	+ "key'); a key-editing verb has no single tenant to be confined to")]
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
		// A sysadmin (this page's policy) may grant privileged scopes — KeyIssuer reads that off the
		// IsSysAdmin claim rather than off the page's identity, so the authority travels with the
		// PRINCIPAL and the service does not have to trust which page called it.
		switch (await keys.UpdateAsync(edit, workspaceKey: null, KeyIssuer.From(User), HttpContext.RequestAborted))
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
