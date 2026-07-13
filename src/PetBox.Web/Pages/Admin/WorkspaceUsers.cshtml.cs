using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Web.Pages.Admin;

// Thin: every membership read and write goes through IWorkspaceMembershipService, which owns the
// core.db access and the "never orphan a workspace" rule. The page only maps outcomes to UI text.
[Authorize(Policy = "WorkspaceAdmin")]
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
	public async Task<IActionResult> OnPostAddAsync(
		[FromRoute(Name = "workspaceKey")] string workspaceKey, string Username, string? Password, WorkspaceRole Role, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(Username))
		{
			ErrorMessage = "Username is required.";
			await LoadMembersAsync(workspaceKey, ct);
			return Page();
		}

		var outcome = await _members.AddMemberAsync(workspaceKey, Username, Password, Role, ct);
		if (outcome == AddMemberOutcome.PasswordRequired)
		{
			ErrorMessage = "A password is required to create a new user.";
			await LoadMembersAsync(workspaceKey, ct);
			return Page();
		}

		// AlreadyMember is a success for the caller: the user IS in the workspace afterwards.
		this.NotifySuccess("Member added.");
		return RedirectToPage();
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
