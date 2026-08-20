using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

// Thin: every membership read and write goes through IWorkspaceMembershipService, which owns the
// core.db access and the "never orphan a workspace" rule. The page only maps outcomes to UI text.
[Authorize(Policy = "WorkspaceAdmin")]
// {workspaceKey} in the route IS the target tenant. ITenantAuthorizer answers both credential
// kinds from that one value — a session on membership of that workspace, an api key on "a project
// claim authorizes its workspace" — so this page needs no check of its own. The MinRole the policy
// above demands is a DIFFERENT axis and stays there.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
public sealed class WorkspaceUsersModel : PageModel
{
	readonly IWorkspaceMembershipService _members;

	public WorkspaceUsersModel(IWorkspaceMembershipService members) => _members = members;

	public IReadOnlyList<WorkspaceMemberRow> Members { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync([FromRoute(Name = "workspaceKey")] string workspaceKey, CancellationToken ct)
	{
		await LoadMembersAsync(workspaceKey, ct);
	}

	async Task LoadMembersAsync(string workspaceKey, CancellationToken ct) =>
		Members = await _members.ListMembersAsync(workspaceKey, ct);

	// authz-bypass-project-create: [FromRoute] pins this to the ROUTE workspace — never a
	// form-supplied "workspaceKey" field, which the default composite provider (Form -> Route ->
	// Query) would otherwise let override the route after the WorkspaceAdmin policy check passed.
	//
	// THE ORACLE MAPPING (add-member-composite-fix, spec composite-write-branch-opaque). The page
	// is where the enumeration oracle was actually READABLE, so this is where it has to be shut.
	// Two rules, and both are load-bearing:
	//
	//  1. `Mode` comes from the admin, in the markup, before the post. The page has TWO add forms
	//     — "create a new account" and "add an existing account" — each with a fixed field set,
	//     so which fields are required is a property of the form the admin chose, never of what
	//     the server found in the Users table.
	//  2. Added, AlreadyMember and NoSuchUser get ONE response: same 302, same notice text, same
	//     Location. Those three are the outcomes that depend on the table, so telling them apart
	//     is exactly the leak. Only the form-shaped refusals (which depend on the POST alone, and
	//     only ever in CreateNew mode) get distinct text.
	public async Task<IActionResult> OnPostAddAsync(
		[FromRoute(Name = "workspaceKey")] string workspaceKey,
		string Username,
		AddMemberMode? Mode,
		string? Password,
		int? WorkspaceQuota,
		WorkspaceRole Role,
		CancellationToken ct)
	{
		// No declared intent = a post this page did not render (or a hand-rolled one). Refusing is
		// safe: it depends on the body only. Guessing a default would silently pick a field set for
		// the caller, which is the habit that grew the oracle in the first place.
		if (Mode is not { } mode)
			return await RefuseAsync(workspaceKey, "Choose whether you are creating a new account or adding an existing one.", ct);

		if (string.IsNullOrWhiteSpace(Username))
			return await RefuseAsync(workspaceKey, "Username is required.", ct);

		var outcome = await _members.AddMemberAsync(workspaceKey, Username, mode, Password, WorkspaceQuota, Role, ct);

		// FORM-SHAPED refusals only. Each is decided from the route/POSTed fields before the service
		// reads a row of Users, so distinct text here reveals nothing about who exists. NoSuchWorkspace
		// reads Workspaces, not Users — same class. In practice this page never sees it: the route's
		// [FromRoute] + the WorkspaceAdmin policy already require workspaceKey to be a real, authorized
		// workspace before OnPostAddAsync runs. The branch exists so an unhandled outcome cannot fall
		// through to the success path below.
		var refusal = outcome switch
		{
			AddMemberOutcome.NoSuchWorkspace => "Workspace not found.",
			AddMemberOutcome.PasswordRequired => "A password is required to create a new account.",
			AddMemberOutcome.QuotaRequired =>
				"Workspace allowance is required — enter 0 if this account may not create workspaces.",
			AddMemberOutcome.QuotaInvalid => "Workspace allowance cannot be negative.",
			_ => null,
		};
		if (refusal is not null)
			return await RefuseAsync(workspaceKey, refusal, ct);

		// TABLE-SHAPED answers — Added, AlreadyMember, NoSuchUser — collapse to ONE response.
		// AlreadyMember is a success for the caller (the account IS in the workspace afterwards);
		// NoSuchUser is phrased conditionally because that is the honest thing this page can say
		// without answering "does this account exist?". The text varies by MODE, which the admin
		// chose, and by nothing else.
		this.NotifySuccess(mode == AddMemberMode.CreateNew
			? "Member added."
			: "Done — if an account with that username exists, it is now a member of this workspace.");
		return RedirectToPage();
	}

	async Task<IActionResult> RefuseAsync(string workspaceKey, string message, CancellationToken ct)
	{
		ErrorMessage = message;
		await LoadMembersAsync(workspaceKey, ct);
		return Page();
	}

	public async Task<IActionResult> OnPostRemoveAsync(
		[FromRoute(Name = "workspaceKey")] string workspaceKey, long userId, CancellationToken ct)
	{
		var outcome = await _members.RemoveMemberAsync(workspaceKey, userId, ct);
		if (outcome == MemberChangeOutcome.LastAdmin)
		{
			ErrorMessage = "Cannot remove the last admin of this workspace.";
			await LoadMembersAsync(workspaceKey, ct);
			return Page();
		}

		// NotFound is a success too — the member is gone either way (idempotent remove).
		this.NotifySuccess("Member removed.");
		return RedirectToPage();
	}

	// workspace-member-role-edit: a workspace left with zero admins is unmanageable by its own
	// members — only a sysadmin could recover it. The service guards both the demote-in-place path
	// and the remove path, which had the same hole.
	public async Task<IActionResult> OnPostSetRoleAsync(
		[FromRoute(Name = "workspaceKey")] string workspaceKey, long userId, WorkspaceRole Role, CancellationToken ct)
	{
		var outcome = await _members.SetRoleAsync(workspaceKey, userId, Role, ct);
		if (outcome != MemberChangeOutcome.Changed)
		{
			ErrorMessage = outcome == MemberChangeOutcome.LastAdmin
				? "Cannot demote the last admin of this workspace."
				: "Member not found.";
			await LoadMembersAsync(workspaceKey, ct);
			return Page();
		}

		this.NotifySuccess("Role updated.");
		return RedirectToPage();
	}
}
