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

// WHAT THE CALLER SAYS IT IS DOING — declared up front, before the server has looked at anything.
//
// This enum is the fix for an account-enumeration oracle (spec composite-write-branch-opaque). The
// method used to DERIVE its shape from the Users table: hand it a name it knew and it added the
// membership, hand it a name it did not and it demanded a password. A workspace admin therefore
// learned, from the answer alone, whether an account exists in this instance — including accounts a
// sysadmin created elsewhere that this admin has no business knowing about.
//
// With the intent DECLARED, the answer is a function of the declaration, not of the table. Every
// refusal is decided from the submitted form before the first row is read, and every non-refusal
// collapses into one class. Two posts of the SAME mode — one naming a real account, one naming a
// name nobody holds — are indistinguishable in status, text, field set, and (see the unconditional
// hash in AddMemberAsync) in wall-clock time.
public enum AddMemberMode
{
	// "I am creating an account that does not exist yet." Password AND workspace allowance are
	// mandatory — the same two decisions UserAdminService.CreateAsync demands, because this creates
	// the same kind of row. If the name turns out to be taken, the existing account simply gains the
	// membership: its password and its allowance are NEVER overwritten, and the caller is told
	// nothing about which of the two happened.
	CreateNew,

	// "I am granting membership to an account that already exists." No password (this must not read
	// as a password reset — that is the sysadmin's verb, not this page's) and no allowance: nothing
	// is created, so there is no allowance to decide. A name nobody holds writes nothing and answers
	// exactly as a name somebody holds does.
	AddExisting,
}

// Why a mutation did not happen — the page turns these into its error text. An enum rather than an
// exception because none of them is exceptional: they are the normal answers to a form post.
//
// ORACLE CONTRACT — read this before adding a member or changing how a page renders these. They
// fall into two groups that must never be mixed:
//
//  * FORM-SHAPED refusals — PasswordRequired, QuotaRequired, QuotaInvalid. Decided from the POSTed
//    fields ALONE, before any read of Users, and only ever in CreateNew mode. They carry zero bits
//    about who exists, so a page may render each with its own text.
//  * TABLE-SHAPED answers — Added, AlreadyMember, NoSuchUser. WHICH of the three comes back does
//    depend on the table, so a page MUST render all three identically: same status, same text, same
//    redirect. WorkspaceUsersModel.OnPostAddAsync is the reference mapping. Rendering NoSuchUser as
//    an error, or AlreadyMember as a warning, re-opens the oracle this enum exists to close.
public enum AddMemberOutcome
{
	// The membership row is there and this call wrote it.
	Added,

	// The membership row was already there. Nothing written; the caller's goal holds either way.
	AlreadyMember,

	// AddExisting mode and no account holds that name, so there was nothing to grant membership to.
	// Nothing written. Indistinguishable from Added/AlreadyMember to the page, ON PURPOSE.
	NoSuchUser,

	// CreateNew mode with no password. An empty PasswordHash cannot authenticate (M008_Users).
	PasswordRequired,

	// CreateNew mode with no workspace allowance. The SAME refusal UserAdminService.CreateAsync
	// makes, for the same reason: "nobody decided" and "decided: none" are different facts and only
	// the second may be written to an account (M044_UserWorkspaceQuota).
	QuotaRequired,

	// CreateNew mode with a negative workspace allowance.
	QuotaInvalid,
}

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

	// Adds `username` to the workspace. `mode` is the CALLER'S DECLARED INTENT, and it — not the
	// contents of the Users table — decides what this call requires and what class of answer it
	// gives. See AddMemberMode, and the oracle contract on AddMemberOutcome.
	//
	// CreateNew needs `password` AND `workspaceQuota`; both are refused when missing, before any
	// read. AddExisting needs neither and ignores both. An EXISTING account keeps its password and
	// its allowance in BOTH modes: a supplied one is ignored, never an overwrite.
	Task<AddMemberOutcome> AddMemberAsync(
		string workspaceKey,
		string username,
		AddMemberMode mode,
		string? password,
		int? workspaceQuota,
		WorkspaceRole role,
		CancellationToken ct = default);

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

	// Three defects lived in this one method; all three are answered here — add-member-composite-fix.
	// Read the three numbered blocks as one design: each undoes a way this method used to leak or
	// lose something.
	public async Task<AddMemberOutcome> AddMemberAsync(
		string workspaceKey,
		string username,
		AddMemberMode mode,
		string? password,
		int? workspaceQuota,
		WorkspaceRole role,
		CancellationToken ct = default)
	{
		// 1. EVERY refusal is decided before the first read.
		//
		// Nothing past this block can refuse. That ordering is the oracle fix itself, not tidiness:
		// a refusal issued after the Users lookup is a refusal that CAN depend on the lookup, and
		// the next edit to this method would make it so without anyone noticing. Decide from the
		// form, then touch the table.
		string? passwordHash = null;
		if (mode == AddMemberMode.CreateNew)
		{
			if (string.IsNullOrWhiteSpace(password))
				return AddMemberOutcome.PasswordRequired;

			// The same two refusals, in the same order and with the same meaning, as
			// UserAdminService.CreateAsync — this branch writes the same row, so it owes the same
			// decision. A missing allowance is a REFUSAL and never a silent 0: an account quietly
			// given 0 can create no workspace of its own, and nobody ever chose that for it
			// (M044_UserWorkspaceQuota).
			if (workspaceQuota is not { } quota)
				return AddMemberOutcome.QuotaRequired;
			if (quota < 0)
				return AddMemberOutcome.QuotaInvalid;

			// Hashed UNCONDITIONALLY, here, before we know whether the name is taken — and thrown
			// away below if it is. PBKDF2 at 100_000 iterations costs tens of milliseconds, far
			// above HTTP jitter: hashing only on the create path would leave "the answer came back
			// fast" as a perfectly readable second oracle, the same secret restated in the time
			// domain after it was closed in the status/text domain.
			passwordHash = AdminPasswordHasher.Hash(password);
		}

		using var db = dbf.Open();

		// 2. Both inserts AND the read between them are one transaction.
		//
		// The account row and the membership row are two halves of one act. Written unwrapped, a
		// failure in the gap (the AlreadyMember read sits right in it) left an account with no
		// membership at all: invisible on this page, unreachable through it, and holding whatever
		// allowance it was created with. Safe HERE specifically because this method calls no other
		// core-db service while the transaction is open — that is the one thing core.db's
		// Cache=Shared answers with an un-retried SQLITE_LOCKED (AGENTS.md, "Database").
		using var tx = await db.BeginTransactionAsync(ct);

		var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
		long userId;
		if (existing is not null)
		{
			// Taken name. In BOTH modes this is a membership grant and nothing else: the account
			// keeps its password (a CreateNew password is discarded, never an overwrite — that
			// would be a password reset the admin did not ask for) and keeps its allowance.
			userId = existing.Id;
		}
		else if (mode == AddMemberMode.AddExisting)
		{
			// Nobody holds this name, and this mode creates nothing. Write nothing, and answer in
			// the same class as the two branches that do write — see the oracle contract on the
			// outcome enum. The caller learns its own goal state, not who exists.
			await tx.RollbackAsync(ct);
			return AddMemberOutcome.NoSuchUser;
		}
		else
		{
			// 3. The allowance is a DECISION carried in, never a CLR default.
			userId = await db.InsertWithInt64IdentityAsync(new User
			{
				Username = username,
				PasswordHash = passwordHash!,
				CreatedAt = DateTime.UtcNow,
				WorkspaceQuota = workspaceQuota!.Value,
			}, token: ct);
		}

		var already = await db.WorkspaceMembers.AnyAsync(
			m => m.UserId == userId && m.WorkspaceKey == workspaceKey, ct);
		if (already)
		{
			// Reachable only for a pre-existing account (a row inserted a few lines up has no
			// memberships yet), so this rollback undoes nothing today. It is here so no future edit
			// can leave the path committing half an act.
			await tx.RollbackAsync(ct);
			return AddMemberOutcome.AlreadyMember;
		}

		await db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = workspaceKey, Role = role }, token: ct);
		await tx.CommitAsync(ct);
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
