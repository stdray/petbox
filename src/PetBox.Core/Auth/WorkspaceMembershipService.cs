using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

// One membership of one user. What WorkspaceClaimsRefresher needs to rebuild yb:ws_roles.
public sealed record WorkspaceMembership(string WorkspaceKey, WorkspaceRole Role);

// One membership as a GLOBAL row — the user included. The sysadmin users table renders every
// account with its memberships inline, so it needs the whole table at once rather than N reads.
public sealed record WorkspaceMemberOf(long UserId, string WorkspaceKey, WorkspaceRole Role);

// One row of the workspace members admin table: the membership plus the username it belongs to
// (the page renders names, not user ids). "?" when the user row is missing.
public sealed record WorkspaceMemberRow(long UserId, string Username, WorkspaceRole Role);

// Why a mutation did not happen — the page turns these into its error text. An enum rather than an
// exception because none of them is exceptional: they are the normal answers to a form post.
public enum AddMemberOutcome { Added, AlreadyMember, PasswordRequired }

// LastAdmin: the change would leave the workspace with zero admins, which makes it unmanageable by
// its own members (only a sysadmin could recover it) — workspace-member-role-edit.
public enum MemberChangeOutcome { Changed, NotFound, LastAdmin }

// Every read AND every write of WorkspaceMembers goes through here.
//
// Three reasons, in order of importance:
//  1. The DB is visible only in the service layer. WorkspaceClaimsRefresher is an IClaimsTransformation
//     — pipeline code that runs on EVERY authenticated request — and it used to open core.db and read
//     SQLite SYNCHRONOUSLY (.ToList()) on the request thread. It now awaits this service instead.
//  2. It is the seam a cache will need. Memberships are read once per authenticated request and
//     written rarely, which is the textbook shape for caching — but a cache is only correct if every
//     writer can invalidate it, so readers and writers must share one door. This is that door.
//     (No cache today — deliberately: the owner decides that separately. Nothing here assumes one.)
//  3. Memberships ARE the workspace-quota ledger (the allowance is spent by Admin rows, see
//     ClaimAdminSlotAsync), so a membership written around this service is an allowance spent — or
//     silently refunded — behind its back.
//
// It lives in PetBox.CORE, not PetBox.Web, because of the WRITERS: two of them — AdminBootstrapper
// (the first-boot $system-admin seed) and WorkspaceProvisioning (self-service workspace creation) —
// are Core types, and a Web service is unreachable from Core. A door half the writers cannot open
// is not a door. Its readers are indifferent to the move: Web already references Core.
public interface IWorkspaceMembershipService
{
	Task<IReadOnlyList<WorkspaceMembership>> GetRolesAsync(long userId, CancellationToken ct = default);

	Task<IReadOnlyList<WorkspaceMemberRow>> ListMembersAsync(string workspaceKey, CancellationToken ct = default);

	// EVERY membership row in the instance — a full-table read, wanted only by the sysadmin users
	// table (one row per account, its memberships and quota usage inline). Not for a request path.
	Task<IReadOnlyList<WorkspaceMemberOf>> ListAllAsync(CancellationToken ct = default);

	Task<int> CountMembersAsync(string workspaceKey, CancellationToken ct = default);

	Task<int> CountAdminsAsync(string workspaceKey, CancellationToken ct = default);

	Task<bool> IsAdminAsync(long userId, string workspaceKey, CancellationToken ct = default);

	// How many workspaces this account OWNS: Admin rows, excluding the seeded $system. This is the
	// number the workspace allowance is spent against (see ClaimAdminSlotAsync) — the users admin
	// table shows it next to the quota so an admin sets a number against a fact, not a guess.
	Task<int> CountOwnedWorkspacesAsync(long userId, CancellationToken ct = default);

	// Adds `username` to the workspace, creating the user when it does not exist (then `password` is
	// mandatory — an empty PasswordHash cannot authenticate, see M008_Users). An EXISTING account
	// keeps its password: a supplied one is ignored, never an overwrite.
	Task<AddMemberOutcome> AddMemberAsync(
		string workspaceKey, string username, string? password, WorkspaceRole role, CancellationToken ct = default);

	Task<MemberChangeOutcome> RemoveMemberAsync(string workspaceKey, long userId, CancellationToken ct = default);

	Task<MemberChangeOutcome> SetRoleAsync(string workspaceKey, long userId, WorkspaceRole role, CancellationToken ct = default);

	// THE quota enforcement point — the count and the insert are ONE statement (see the impl for why
	// that is not a style choice). Returns rows affected: 1 = the slot is claimed and the account is
	// now the workspace's Admin, 0 = refused (quota exhausted, or already a member).
	Task<int> ClaimAdminSlotAsync(long userId, string workspaceKey, bool bypassQuota, CancellationToken ct = default);

	// Hand a claimed slot back — the compensating write for a create that failed AFTER the claim.
	// UNGUARDED on purpose: it must be able to remove the only admin of a workspace that never came
	// to exist, which is exactly what RemoveMemberAsync's LastAdmin rule forbids.
	Task<int> ReleaseSlotAsync(long userId, string workspaceKey, CancellationToken ct = default);

	// Cascade: every membership of a workspace that is being deleted. Unguarded (the workspace is
	// going away, so "it would have no admin" is not a defect) — and mandatory, because the rows are
	// the quota ledger: leaving them behind turns an allowance into a one-shot ticket.
	Task<int> RemoveWorkspaceAsync(string workspaceKey, CancellationToken ct = default);

	// Cascade: every membership of a user that is being deleted. Unguarded for the same reason; the
	// "never delete the last sysadmin" rule is enforced by IUserAdminService before it gets here.
	Task<int> RemoveUserAsync(long userId, CancellationToken ct = default);
}

// RS0030 exempt — THE owner. This class is the one place entitled to touch WorkspaceMembers; the ban
// exists to make every OTHER caller come through the interface above. The pragma is the door, and it
// opens exactly here.
#pragma warning disable RS0030
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

	public async Task<IReadOnlyList<WorkspaceMemberOf>> ListAllAsync(CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers
			.OrderBy(m => m.WorkspaceKey)
			.Select(m => new WorkspaceMemberOf(m.UserId, m.WorkspaceKey, m.Role))
			.ToListAsync(ct);
	}

	public async Task<int> CountMembersAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers.CountAsync(m => m.WorkspaceKey == workspaceKey, ct);
	}

	public async Task<int> CountAdminsAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers.CountAsync(
			m => m.WorkspaceKey == workspaceKey && m.Role == WorkspaceRole.Admin, ct);
	}

	public async Task<bool> IsAdminAsync(long userId, string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers.AnyAsync(
			m => m.UserId == userId && m.WorkspaceKey == workspaceKey && m.Role == WorkspaceRole.Admin, ct);
	}

	public async Task<int> CountOwnedWorkspacesAsync(long userId, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await OwnedWorkspaces(db, userId).CountAsync(ct);
	}

	// The quota's definition of ownership, in ONE place — the same expression the atomic claim below
	// compares against, so "how many do I own" and "may I take one more" can never disagree.
	//
	// The model records no creator, deliberately: ownership IS the Admin role — that is what creation
	// grants (spec workspace-creator-is-admin). "$system" is excluded: nobody created it (M004 seeds
	// it), and being a sysadmin is not a workspace someone spent an allowance on.
	static IQueryable<WorkspaceMember> OwnedWorkspaces(PetBoxDb db, long userId) =>
		db.WorkspaceMembers.Where(m =>
			m.UserId == userId
			&& m.Role == WorkspaceRole.Admin
			&& m.WorkspaceKey != WorkspaceMemory.SystemWorkspace);

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

	// Claim one workspace slot for this account, ATOMICALLY: the quota check IS the insert, so there
	// is no instant between "may I?" and "done" for a second request to slip into.
	//
	// It reads as a query and it compiles to `INSERT INTO WorkspaceMembers (…) SELECT … FROM Users
	// WHERE Id = @uid AND NOT EXISTS (…) AND (SELECT COUNT(*) …) < Users.WorkspaceQuota` — ONE
	// statement. The Users row is the SOURCE of the insert rather than a separate lookup, which is
	// what lets the account's quota be compared inside the same statement that consumes it; it also
	// means an unknown account selects no rows and therefore claims nothing, which is the right answer
	// for an identity that is not there.
	//
	// NEVER take this apart into a check and an insert, in any refactor, for any reason. Eight
	// simultaneous posts from an account with an allowance of 1 produced eight workspaces, every
	// single time, back when the check lived in an `if` above the write — WorkspaceSelfProvisioning
	// Tests fires exactly that volley. And no transaction, deliberately: core.db runs Cache=Shared and
	// the SQLITE_LOCKED it raises is not retried by the busy handler; a single self-contained
	// statement needs none to be atomic, which is precisely why the condition had to move INSIDE it.
	//
	// spec workspace-creator-is-admin: the row this writes IS the creator's Admin membership, so the
	// claim and the grant are one act. bypassQuota is the sysadmin's free pass (the quota is not their
	// leash) — their claim carries no quota clause, but it still goes through this one statement, so
	// there is one way to become a workspace's admin and not two.
	public async Task<int> ClaimAdminSlotAsync(
		long userId, string workspaceKey, bool bypassQuota, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var source = db.Users
			.Where(u => u.Id == userId)
			.Where(u => !db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == workspaceKey));

		if (!bypassQuota)
			source = source.Where(u => OwnedWorkspaces(db, userId).Count() < u.WorkspaceQuota);

		// AWAITED inside the `using`: returning the Task unawaited would dispose the connection the
		// statement is still running on.
		return await source.InsertAsync(
			db.WorkspaceMembers,
			u => new WorkspaceMember
			{
				UserId = userId,
				WorkspaceKey = workspaceKey,
				Role = WorkspaceRole.Admin,
			},
			ct);
	}

	public async Task<int> ReleaseSlotAsync(long userId, string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers
			.Where(m => m.UserId == userId && m.WorkspaceKey == workspaceKey)
			.DeleteAsync(ct);
	}

	public async Task<int> RemoveWorkspaceAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers.Where(m => m.WorkspaceKey == workspaceKey).DeleteAsync(ct);
	}

	public async Task<int> RemoveUserAsync(long userId, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.WorkspaceMembers.Where(m => m.UserId == userId).DeleteAsync(ct);
	}

	static async Task<bool> IsLastAdminAsync(PetBoxDb db, string workspaceKey, CancellationToken ct) =>
		await db.WorkspaceMembers
			.CountAsync(m => m.WorkspaceKey == workspaceKey && m.Role == WorkspaceRole.Admin, ct) <= 1;
}
#pragma warning restore RS0030
