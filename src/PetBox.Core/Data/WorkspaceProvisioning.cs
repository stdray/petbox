using LinqToDB;
using PetBox.Core.Models;

namespace PetBox.Core.Data;

// Creating a workspace, in ONE place.
//
// The steps are not optional and not obvious — insert the row, provision its memory container, make
// the creator its Admin (spec workspace-creator-is-admin) — and they used to live inline in the
// sysadmin page. Self-service (spec workspace-create-permission) is a second entry point into the
// SAME act, so the steps moved here rather than being copied: a workspace that exists but has no
// memory container, or none with an admin, is a broken workspace, and the way to make that
// unexpressible is to leave callers no way to perform half of it.
//
// The quota check lives here too, for the same reason: it is enforced on the WRITE, not by whoever
// happened to render the button. A caller cannot forget it — it must actively pass
// bypassQuota: true (which only a sysadmin's request may do).
public static class WorkspaceProvisioning
{
	// Why the outcome is a value and not an exception: both callers are Razor page handlers that
	// re-render the form with the message. An exception would be control flow for the expected case.
	public sealed record Result(bool Ok, string? Error)
	{
		public static Result Success() => new(true, null);
		public static Result Fail(string error) => new(false, error);
	}

	public const string KeyRuleMessage =
		"Workspace key must match ^[a-z0-9][a-z0-9-]*$ (lowercase letters, digits, hyphens; "
		+ "no '$' prefix — that is reserved for built-in containers; 'sys' is reserved).";

	// How many workspaces a user has already created.
	//
	// The model records no creator, and it deliberately stays that way: the honest question the quota
	// asks is "how many workspaces does this account already OWN", and ownership IS the Admin role —
	// that is what creation grants (workspace-creator-is-admin) and what an admin transferring a
	// workspace would grant. Counting an explicit CreatedBy column instead would let a user shed a
	// workspace (hand over their Admin, keep the row's authorship) and get no quota back, or keep
	// admin of ten workspaces someone else created while their quota still reads "0 used".
	//
	// "$system" is excluded: nobody created it (it is seeded by M004), and it is the sysadmin marker
	// — a sysadmin is not spending quota by being one. Sysadmins bypass the quota anyway.
	public static Task<int> CountOwnedWorkspacesAsync(PetBoxDb db, long userId, CancellationToken ct = default) =>
		db.WorkspaceMembers.CountAsync(
			m => m.UserId == userId
				&& m.Role == WorkspaceRole.Admin
				&& m.WorkspaceKey != WorkspaceMemory.SystemWorkspace,
			ct);

	// True when this account may create ONE more workspace: its explicit quota still exceeds the
	// number it already owns. A missing user is false (not an exception) — an authorization question
	// answers "no" for an identity that is not there.
	public static async Task<bool> CanCreateAsync(PetBoxDb db, long userId, CancellationToken ct = default)
	{
		var quota = await db.Users
			.Where(u => u.Id == userId)
			.Select(u => (int?)u.WorkspaceQuota)
			.FirstOrDefaultAsync(ct);
		if (quota is not { } q || q <= 0) return false;
		return q > await CountOwnedWorkspacesAsync(db, userId, ct);
	}

	// Create a workspace and hand it to its creator, or say why not.
	//
	// bypassQuota is the sysadmin free-pass (a sysadmin may create without limit — the quota is not
	// their leash). Every other caller passes false and is checked against the account's number.
	public static async Task<Result> CreateAsync(
		PetBoxDb db,
		string? key,
		string? name,
		string? description,
		long? creatorUserId,
		bool bypassQuota,
		CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
			return Result.Fail("Key and Name are required.");

		key = key.Trim();
		name = name.Trim();

		// Allowlist before insert: keys become URL segments and `$ws-{key}` file paths, and a leading
		// '$' would collide with the reserved "$system" / "$workspace" containers (spec
		// reserved-workspace-project). The regex admits only ^[a-z0-9][a-z0-9-]*$, so '$' is rejected
		// by construction — this is the gate that makes the user-chosen key safe.
		if (!WorkspaceMemory.IsCreatableWorkspaceKey(key))
			return Result.Fail(KeyRuleMessage);

		if (await db.Workspaces.AnyAsync(w => w.Key == key, ct))
			return Result.Fail($"Workspace '{key}' already exists.");

		// The quota is checked HERE, against the DB, on the write path — the page's own gate (hiding
		// the button, the CanCreateWorkspace policy on the GET) is a courtesy to the UI, not the
		// enforcement. A direct POST lands on this line.
		if (!bypassQuota)
		{
			if (creatorUserId is not { } uid)
				return Result.Fail("Only a signed-in account can create a workspace.");
			if (!await CanCreateAsync(db, uid, ct))
				return Result.Fail("Your account has no remaining workspace allowance. Ask an administrator to raise it.");
		}

		await db.InsertAsync(new Workspace
		{
			Key = key,
			Name = name,
			Description = description?.Trim() ?? string.Empty,
			CreatedAt = DateTime.UtcNow,
		}, token: ct);

		// Provision the workspace memory container so Shared-memory nav works immediately
		// (without waiting for the first MCP write or dashboard ensure).
		await WorkspaceMemory.EnsureContainerAsync(db, key, ct);

		// spec workspace-creator-is-admin: the creator is the workspace's Admin from the moment it
		// exists. No re-login is needed for it to take effect — WorkspaceClaimsRefresher rebuilds the
		// membership claims from the DB on every request.
		if (creatorUserId is { } creator)
		{
			var alreadyMember = await db.WorkspaceMembers
				.AnyAsync(m => m.UserId == creator && m.WorkspaceKey == key, ct);
			if (!alreadyMember)
			{
				await db.InsertAsync(new WorkspaceMember
				{
					UserId = creator,
					WorkspaceKey = key,
					Role = WorkspaceRole.Admin,
				}, token: ct);
			}
		}

		return Result.Success();
	}
}
