using LinqToDB;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Auth;

// One membership of one user. What WorkspaceClaimsRefresher needs to rebuild yb:ws_roles.
public sealed record WorkspaceMembership(string WorkspaceKey, WorkspaceRole Role);

// One row of the workspace members admin table: the membership plus the username it belongs to
// (the page renders names, not user ids). "?" when the user row is missing.
public sealed record WorkspaceMemberRow(long UserId, string Username, WorkspaceRole Role);

// Why a mutation did not happen — the page turns these into its error text. An enum rather than an
// exception because none of them is exceptional: they are the normal answers to a form post.
public enum AddMemberOutcome { Added, AlreadyMember, PasswordRequired }

// LastAdmin: the change would leave the workspace with zero admins, which makes it unmanageable by
// its own members (only a sysadmin could recover it) — workspace-member-role-edit.
public enum MemberChangeOutcome { Changed, NotFound, LastAdmin }

// Every read AND every write of WorkspaceMembers that the web layer performs goes through here.
//
// Two reasons, in order of importance:
//  1. The DB is visible only in the service layer. WorkspaceClaimsRefresher is an IClaimsTransformation
//     — pipeline code that runs on EVERY authenticated request — and it used to open core.db and read
//     SQLite SYNCHRONOUSLY (.ToList()) on the request thread. It now awaits this service instead.
//  2. It is the seam a cache will need. Memberships are read once per authenticated request and
//     written rarely, which is the textbook shape for caching — but a cache is only correct if every
//     writer can invalidate it, so readers and writers must share one door. This is that door.
//     (No cache today — deliberately: the owner decides that separately. Nothing here assumes one.)
public interface IWorkspaceMembershipService
{
	Task<IReadOnlyList<WorkspaceMembership>> GetRolesAsync(long userId, CancellationToken ct = default);

	Task<IReadOnlyList<WorkspaceMemberRow>> ListMembersAsync(string workspaceKey, CancellationToken ct = default);

	// Adds `username` to the workspace, creating the user when it does not exist (then `password` is
	// mandatory — an empty PasswordHash cannot authenticate, see M008_Users). An EXISTING account
	// keeps its password: a supplied one is ignored, never an overwrite.
	Task<AddMemberOutcome> AddMemberAsync(
		string workspaceKey, string username, string? password, WorkspaceRole role, CancellationToken ct = default);

	Task<MemberChangeOutcome> RemoveMemberAsync(string workspaceKey, long userId, CancellationToken ct = default);

	Task<MemberChangeOutcome> SetRoleAsync(string workspaceKey, long userId, WorkspaceRole role, CancellationToken ct = default);
}

public sealed class WorkspaceMembershipService(ICoreDbFactory dbf) : IWorkspaceMembershipService
{
	public async Task<IReadOnlyList<WorkspaceMembership>> GetRolesAsync(long userId, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers
			.Where(m => m.UserId == userId)
			.Select(m => new WorkspaceMembership(m.WorkspaceKey, m.Role))
			.ToListAsync(ct);
	}

	public async Task<IReadOnlyList<WorkspaceMemberRow>> ListMembersAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		// Left join: a membership whose User row vanished must still render (and be removable),
		// not disappear from the admin table that is the only way to clean it up.
		var rows = await (
			from m in db.WorkspaceMembers
			where m.WorkspaceKey == workspaceKey
			from u in db.Users.LeftJoin(u => u.Id == m.UserId)
			select new { m.UserId, m.Role, u.Username }).ToListAsync(ct);

		return [.. rows.Select(r => new WorkspaceMemberRow(r.UserId, r.Username ?? "?", r.Role))];
	}

	public async Task<AddMemberOutcome> AddMemberAsync(
		string workspaceKey, string username, string? password, WorkspaceRole role, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
		long userId;
		if (existing is not null)
		{
			userId = existing.Id;
		}
		else
		{
			if (string.IsNullOrWhiteSpace(password))
				return AddMemberOutcome.PasswordRequired;

			userId = await db.InsertWithInt64IdentityAsync(new User
			{
				Username = username,
				PasswordHash = AdminPasswordHasher.Hash(password),
				CreatedAt = DateTime.UtcNow,
			}, token: ct);
		}

		var already = await db.WorkspaceMembers.AnyAsync(
			m => m.UserId == userId && m.WorkspaceKey == workspaceKey, ct);
		if (already)
			return AddMemberOutcome.AlreadyMember;

		await db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = workspaceKey, Role = role }, token: ct);
		return AddMemberOutcome.Added;
	}

	public async Task<MemberChangeOutcome> RemoveMemberAsync(string workspaceKey, long userId, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var member = await db.WorkspaceMembers.FirstOrDefaultAsync(
			m => m.UserId == userId && m.WorkspaceKey == workspaceKey, ct);
		if (member is null)
			return MemberChangeOutcome.NotFound;

		if (member.Role == WorkspaceRole.Admin && await IsLastAdminAsync(db, workspaceKey, ct))
			return MemberChangeOutcome.LastAdmin;

		await db.WorkspaceMembers
			.Where(m => m.UserId == userId && m.WorkspaceKey == workspaceKey)
			.DeleteAsync(ct);
		return MemberChangeOutcome.Changed;
	}

	public async Task<MemberChangeOutcome> SetRoleAsync(
		string workspaceKey, long userId, WorkspaceRole role, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var member = await db.WorkspaceMembers.FirstOrDefaultAsync(
			m => m.UserId == userId && m.WorkspaceKey == workspaceKey, ct);
		if (member is null)
			return MemberChangeOutcome.NotFound;

		// Only a DEMOTION of the last admin orphans the workspace — re-setting them to Admin is a no-op
		// that must stay allowed.
		if (member.Role == WorkspaceRole.Admin && role != WorkspaceRole.Admin && await IsLastAdminAsync(db, workspaceKey, ct))
			return MemberChangeOutcome.LastAdmin;

		await db.WorkspaceMembers
			.Where(m => m.UserId == userId && m.WorkspaceKey == workspaceKey)
			.Set(m => m.Role, role)
			.UpdateAsync(ct);
		return MemberChangeOutcome.Changed;
	}

	static async Task<bool> IsLastAdminAsync(PetBoxDb db, string workspaceKey, CancellationToken ct) =>
		await db.WorkspaceMembers
			.CountAsync(m => m.WorkspaceKey == workspaceKey && m.Role == WorkspaceRole.Admin, ct) <= 1;
}
