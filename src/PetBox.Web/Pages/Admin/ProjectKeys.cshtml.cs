using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// admin-routes-and-pages (item 3): API-key management, split off the project Info page — that page
// was overloaded (health endpoints, keys, log retention, commit links, danger zone all on one
// screen). This page owns create/list/revoke/edit-scopes for a project's DB-minted keys, using the
// SAME project-confined pair ProjectDetail used to (AgentKeyAdminService.ListByProjectAsync /
// MintAsync / RevokeForProjectAsync / SetScopesForProjectAsync — see the service's own comment on
// why those are confined to the PROJECT, not the workspace: an admin of a dozen projects must not be
// able to touch a sibling project's key via a forged POST naming it).
//
// Deliberately separate from ProjectConnect (/connect): that page is the onboarding-flavored "issue
// ONE key, then show wiring instructions" flow; this one is the ongoing management surface (see
// every key, revoke what leaked, edit scopes in place) — the same split WorkspaceAgentKeysModel
// already draws at the workspace scope (workspace-admin-owns-keys).
//
// WorkspaceKey/ProjectKey are [FromRoute] ONLY, never a plain property (authz-bypass-project-create):
// the WorkspaceAdmin policy checks the ROUTE workspace, and ASP.NET's default composite binding order
// (Form -> Route -> Query) would otherwise let a POST body field named "workspaceKey"/"projectKey"
// override it after the policy already passed. Cross-project confinement is ALSO welded into the
// service's UPDATE/DELETE statements themselves (RevokeForProjectAsync/SetScopesForProjectAsync), not
// merely into what this page renders — so a forged POST naming a sibling project's key matches zero
// rows even if this page's own route binding were somehow bypassed.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectKeysModel : PageModel
{
	readonly IProjectDirectory _projects;
	readonly AgentKeyAdminService _keys;

	public ProjectKeysModel(IProjectDirectory projects, AgentKeyAdminService keys)
	{
		_projects = projects;
		_keys = keys;
	}

	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	[FromRoute(Name = "projectKey")]
	public string ProjectKey { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public IReadOnlyList<ApiKey> Keys { get; private set; } = [];
	public string? ErrorMessage { get; set; }
	public string? NewKey { get; set; }

	public async Task OnGetAsync()
	{
		Project = await _projects.GetAsync(ProjectKey);
		if (Project is null) return;

		// A just-minted key rides here across the Post/Redirect/Get from OnPostCreateKey and is
		// shown once; a refresh (no TempData) drops it. See Notice.CarryNewKey.
		NewKey = this.TakeNewKey();

		// Newest first — same rendering choice ProjectDetail made over the service's chronological
		// (MCP-caller-friendly) order.
		Keys = [.. (await _keys.ListByProjectAsync(ProjectKey)).OrderByDescending(k => k.CreatedAt)];
	}

	RedirectResult Self() => Redirect(Routes.ProjectKeys(WorkspaceKey, ProjectKey));

	public async Task<IActionResult> OnPostCreateKeyAsync(string name, string[]? scopes)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			ErrorMessage = "Name is required.";
			await OnGetAsync();
			return Page();
		}

		// Scopes are canonicalized before the mint — AgentKeyAdminService.MintAsync takes an already
		// validated set (the same contract its MCP callers honour), and the checkbox list is the only
		// intended input, so a typed one gets told so.
		var raw = scopes is null ? "" : string.Join(",", scopes);
		var (valid, invalid) = ApiKeyScopes.Validate(raw);
		if (invalid.Count > 0)
		{
			ErrorMessage = "Unknown scope(s): " + string.Join(", ", invalid)
				+ ". Pick from the checkbox list — typed input is not supported.";
			await OnGetAsync();
			return Page();
		}
		if (valid.Count == 0)
		{
			ErrorMessage = "At least one scope is required.";
			await OnGetAsync();
			return Page();
		}

		var minted = await _keys.MintAsync(new AgentKeyMint(name, valid, ProjectKey));
		switch (minted)
		{
			case KeyMintResult.Minted m:
				// PRG: carry the one-time key across a redirect to the clean keys URL (no lingering
				// ?handler=CreateKey a refresh would re-POST) — the key still shows exactly once.
				this.CarryNewKey(m.Key.Key);
				return Self();
			case KeyMintResult.NotFound nf:
				ErrorMessage = nf.Reason;
				await OnGetAsync();
				return Page();
			default:
				ErrorMessage = ((KeyMintResult.Refused)minted).Reason;
				await OnGetAsync();
				return Page();
		}
	}

	public async Task<IActionResult> OnPostRevokeKeyAsync(string keyValue)
	{
		// Project-confined inside the DELETE: a forged POST naming a SIBLING project's key (this admin
		// may hold WorkspaceAdmin over a dozen) matches zero rows.
		await _keys.RevokeForProjectAsync(keyValue, ProjectKey);
		this.NotifySuccess("API key revoked.");
		return Self();
	}

	// Edit the scopes of an existing key in place (scopes were previously fixed at
	// mint time — finding D5). Same validation as minting: known scopes, at least one — enforced in
	// the service, so this page cannot forget it and neither can the next caller.
	public async Task<IActionResult> OnPostUpdateKeyScopesAsync(string keyValue, string[]? scopes)
	{
		var result = await _keys.SetScopesForProjectAsync(keyValue, ProjectKey, scopes ?? []);
		if (result is KeyUpdateResult.Refused refused)
		{
			ErrorMessage = refused.Reason;
			await OnGetAsync();
			return Page();
		}

		this.NotifySuccess("Key scopes updated.");
		return Self();
	}
}
