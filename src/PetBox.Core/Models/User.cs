namespace PetBox.Core.Models;

public sealed record User
{
	public long Id { get; init; }
	public string Username { get; init; } = string.Empty;
	public string PasswordHash { get; init; } = string.Empty;
	public DateTime CreatedAt { get; init; }

	// spec workspace-create-permission: how many workspaces this account may create, as an explicit
	// NUMBER on the account — not a role, and not a boolean. 0 = may not create any. The account is
	// the only place the right lives: it is granted at account creation (explicitly — the admin form
	// has no default) and changed by a sysadmin, never inferred from a role in some workspace.
	//
	// It is a QUOTA, not a licence that can be revoked retroactively: lowering it below the number of
	// workspaces already created leaves those workspaces — and the creator's Admin role in them —
	// untouched (revoking that would leave a workspace with no admin). It only stops the NEXT create.
	public int WorkspaceQuota { get; init; }
}
