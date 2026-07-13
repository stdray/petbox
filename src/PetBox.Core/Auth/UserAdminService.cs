using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

// One account, as the sysadmin users table renders it. The PasswordHash never leaves the service —
// it is not on this record, so a page cannot accidentally put it on a wire.
//
// `IsBootstrapAdmin` and `WorkspacesOwned` are DERIVED, not stored: the first says this row is the
// env-declared bootstrap account (AdminOptions.Username), the second is the quota's own count of
// what the allowance has already been spent on. Both are computed HERE because both are rules, and
// a rule that lives in a page is a rule the next page forgets.
public sealed record UserAccount(
	long Id,
	string Username,
	DateTime CreatedAt,
	int WorkspaceQuota,
	bool IsBootstrapAdmin,
	int WorkspacesOwned,
	IReadOnlyList<WorkspaceMembership> Memberships);

// The outcome of an account mutation. `Refused` carries the reason because a refusal nobody can see
// is a silent failure; `NotFound` is a separate answer so the caller can 404 rather than explain.
public abstract record UserChangeResult
{
	UserChangeResult() { }

	public sealed record Changed : UserChangeResult;
	public sealed record NotFound : UserChangeResult;
	public sealed record Refused(string Reason) : UserChangeResult;
}

// The accounts service: everything the sysadmin users page does to a User row, plus the first-boot
// admin seed. Users and WorkspaceMembers are entangled by three rules that must not be re-derived
// per caller — the workspace allowance is spent by Admin memberships, the last $system admin may
// never be deleted, and deleting an account must take its memberships with it (they are the quota
// ledger) — so all of them live here, welded to the writes they guard.
//
// It lives in PetBox.CORE for the same reason IWorkspaceMembershipService does: AdminBootstrapper's
// first-boot seed is a Core caller (EnsureBootstrapAdminAsync below is its service door), and a Web
// service cannot be reached from Core.
public interface IUserAdminService
{
	Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default);

	Task<UserAccount?> GetAsync(long userId, CancellationToken ct = default);

	// True for the env-declared bootstrap account (AdminOptions.Username). It may not be deleted from
	// the admin UI: it is the recovery path back into an instance whose own admins are gone.
	bool IsBootstrapAdmin(string username);

	// `workspaceQuota` is deliberately NULLABLE and has no default: the form ships the field empty and
	// the admin must type a number (spec workspace-create-permission — the right is granted
	// explicitly). A missing value is a refusal, NOT a silent 0: "nobody decided" and "decided: none"
	// are different facts and only the second may be written to an account.
	Task<UserChangeResult> CreateAsync(string? username, string? password, int? workspaceQuota, CancellationToken ct = default);

	// Raising or lowering an existing allowance. Lowering it (even to 0) does NOT touch the workspaces
	// the account already created, nor its Admin role in them — that would leave a workspace with no
	// administrator. It governs only the NEXT create.
	Task<UserChangeResult> SetQuotaAsync(long userId, int? workspaceQuota, CancellationToken ct = default);

	Task<UserChangeResult> ResetPasswordAsync(long userId, string? newPassword, CancellationToken ct = default);

	// `actingUserId` is the signed-in admin performing the delete — an account may not delete itself
	// (an admin who does is locked out of their own instance). Takes the account's memberships with
	// it, through IWorkspaceMembershipService, so the quota ledger stays honest.
	Task<UserChangeResult> DeleteAsync(long userId, long actingUserId, CancellationToken ct = default);

	// The first-boot seed: create the env-declared admin account and make it the $system Admin — but
	// ONLY while the instance has no $system administrator at all. Once you have made your own admin,
	// the env account is never re-created or refreshed (Login refuses its credentials;
	// PETBOX_ADMIN_FORCE re-enables it for recovery). Idempotent and safe under a concurrent
	// first boot. Returns true when THIS call seeded the admin.
	//
	// This is AdminBootstrapper's door into the service layer. It is a service method rather than a
	// static over a borrowed connection because that static is the last writer of WorkspaceMembers
	// outside the membership service — and a cache over memberships can only be correct if every
	// writer is reachable from the place that would invalidate it.
	Task<bool> EnsureBootstrapAdminAsync(CancellationToken ct = default);
}

public sealed class UserAdminService(
	ICoreDbFactory dbf,
	IOptions<AdminOptions> adminOptions,
	IWorkspaceMembershipService members) : IUserAdminService
{
	// The memberships of EVERY account are read once (ListAllAsync) and grouped in memory rather than
	// re-queried per user: the table renders every account, so N+1 reads of a table that is read on
	// every render is exactly the shape this refactor exists to make cacheable.
	public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken ct = default)
	{
		List<User> users;
		using (var db = dbf.Open())
			users = await db.Users.OrderBy(u => u.Username).ToListAsync(ct);

		var all = await members.ListAllAsync(ct);
		var byUser = all
			.GroupBy(m => m.UserId)
			.ToDictionary(g => g.Key, g => (IReadOnlyList<WorkspaceMemberOf>)[.. g]);

		return [.. users.Select(u => Compose(u, byUser.GetValueOrDefault(u.Id, [])))];
	}

	public async Task<UserAccount?> GetAsync(long userId, CancellationToken ct = default)
	{
		User? user;
		using (var db = dbf.Open())
			user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
		if (user is null) return null;

		var roles = await members.GetRolesAsync(userId, ct);
		return Compose(user, [.. roles.Select(r => new WorkspaceMemberOf(userId, r.WorkspaceKey, r.Role))]);
	}

	UserAccount Compose(User u, IReadOnlyList<WorkspaceMemberOf> memberships) => new(
		u.Id,
		u.Username,
		u.CreatedAt,
		u.WorkspaceQuota,
		IsBootstrapAdmin(u.Username),
		// Exactly the criterion the quota is ENFORCED by (WorkspaceMembershipService.OwnedWorkspaces):
		// Admin rows excluding the seeded $system. Counting it any other way here would show an admin
		// a "used" number the enforcement disagrees with.
		memberships.Count(m => m.Role == WorkspaceRole.Admin && m.WorkspaceKey != WorkspaceMemory.SystemWorkspace),
		[.. memberships
			.OrderBy(m => m.WorkspaceKey, StringComparer.Ordinal)
			.Select(m => new WorkspaceMembership(m.WorkspaceKey, m.Role))]);

	public bool IsBootstrapAdmin(string username) =>
		!string.IsNullOrEmpty(adminOptions.Value.Username)
		&& string.Equals(username, adminOptions.Value.Username, StringComparison.Ordinal);

	public async Task<UserChangeResult> CreateAsync(
		string? username, string? password, int? workspaceQuota, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
			return new UserChangeResult.Refused("Username and password are required.");

		if (workspaceQuota is not { } quota)
			return new UserChangeResult.Refused(
				"Workspace allowance is required — enter 0 if this account may not create workspaces.");

		if (quota < 0)
			return new UserChangeResult.Refused("Workspace allowance cannot be negative.");

		var name = username.Trim();

		using var db = dbf.Open();
		if (await db.Users.AnyAsync(u => u.Username == name, ct))
			return new UserChangeResult.Refused($"User '{name}' already exists.");

		await db.InsertWithInt64IdentityAsync(new User
		{
			Username = name,
			PasswordHash = AdminPasswordHasher.Hash(password),
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = quota,
		}, token: ct);

		return new UserChangeResult.Changed();
	}

	public async Task<UserChangeResult> SetQuotaAsync(long userId, int? workspaceQuota, CancellationToken ct = default)
	{
		if (workspaceQuota is not { } quota || quota < 0)
			return new UserChangeResult.Refused("Workspace allowance must be a number of 0 or more.");

		using var db = dbf.Open();
		var affected = await db.Users
			.Where(u => u.Id == userId)
			.Set(u => u.WorkspaceQuota, quota)
			.UpdateAsync(ct);

		return affected > 0 ? new UserChangeResult.Changed() : new UserChangeResult.NotFound();
	}

	public async Task<UserChangeResult> ResetPasswordAsync(long userId, string? newPassword, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(newPassword))
			return new UserChangeResult.Refused("New password is required.");

		using var db = dbf.Open();
		var affected = await db.Users
			.Where(u => u.Id == userId)
			.Set(u => u.PasswordHash, AdminPasswordHasher.Hash(newPassword))
			.UpdateAsync(ct);

		return affected > 0 ? new UserChangeResult.Changed() : new UserChangeResult.NotFound();
	}

	public async Task<UserChangeResult> DeleteAsync(long userId, long actingUserId, CancellationToken ct = default)
	{
		string username;
		using (var db = dbf.Open())
		{
			var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
			if (user is null) return new UserChangeResult.NotFound();
			username = user.Username;
		}

		if (userId == actingUserId)
			return new UserChangeResult.Refused("You cannot delete your own account.");

		if (IsBootstrapAdmin(username))
			return new UserChangeResult.Refused("The bootstrap admin account cannot be deleted from here.");

		// A user is a sysadmin iff they hold $system/Admin. Deleting the last one leaves an instance
		// nobody can administer — and nothing short of the env bootstrap account can recover it.
		if (await members.IsAdminAsync(userId, WorkspaceMemory.SystemWorkspace, ct)
			&& await members.CountAdminsAsync(WorkspaceMemory.SystemWorkspace, ct) <= 1)
			return new UserChangeResult.Refused("Cannot delete the last system administrator.");

		// Memberships first, through their own service — they are the quota ledger, and this is the
		// door a cache would invalidate from. No transaction spans the two: core.db runs Cache=Shared
		// and calling another core-db service while holding an open transaction raises an un-retried
		// SQLITE_LOCKED. The page did the same two statements unwrapped, so this is no new window.
		await members.RemoveUserAsync(userId, ct);

		using (var db = dbf.Open())
			await db.Users.Where(u => u.Id == userId).DeleteAsync(ct);

		return new UserChangeResult.Changed();
	}

	public async Task<bool> EnsureBootstrapAdminAsync(CancellationToken ct = default)
	{
		var admin = adminOptions.Value;
		if (string.IsNullOrWhiteSpace(admin.Username) || string.IsNullOrWhiteSpace(admin.PasswordHash))
			return false;

		// Cheap fast-path OUTSIDE any transaction: the common case (every boot after the first) never
		// touches the write lock. The seed itself is AdminBootstrapper's, transaction and unique-index
		// backstop included — this is its door into the service layer, not a second implementation of
		// it, so there is exactly one first-boot seed and it keeps its race test.
		using var db = dbf.Open();
		if (await db.WorkspaceMembers.AnyAsync(
			m => m.WorkspaceKey == WorkspaceMemory.SystemWorkspace && m.Role == WorkspaceRole.Admin, ct))
			return false;

		AdminBootstrapper.EnsureAdminUser(db, adminOptions);

		return await db.WorkspaceMembers.AnyAsync(
			m => m.WorkspaceKey == WorkspaceMemory.SystemWorkspace && m.Role == WorkspaceRole.Admin, ct);
	}
}
