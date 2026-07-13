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
//
// A SERVICE, not a static helper over a caller's connection: core.db enters through ICoreDbFactory
// and goes no further. Page handlers used to open a PetBoxDb and hand it in, which put the database
// in the UI layer and made the connection's lifetime a Razor page's business. Now the only thing a
// caller holds is this service, and every statement below runs on a connection this class opened and
// closed. (Never construct PetBoxDb by hand — the factory carries the shared MappingSchema.)
public sealed class WorkspaceProvisioning(ICoreDbFactory dbf)
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

	public const string QuotaExhaustedMessage =
		"Your account has no remaining workspace allowance. Ask an administrator to raise it.";

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
	public async Task<int> CountOwnedWorkspacesAsync(long userId, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await OwnedWorkspaces(db, userId).CountAsync(ct);
	}

	static IQueryable<WorkspaceMember> OwnedWorkspaces(PetBoxDb db, long userId) =>
		db.WorkspaceMembers.Where(m =>
			m.UserId == userId
			&& m.Role == WorkspaceRole.Admin
			&& m.WorkspaceKey != WorkspaceMemory.SystemWorkspace);

	// True when this account may create ONE more workspace: its explicit quota still exceeds the
	// number it already owns. A missing user is false (not an exception) — an authorization question
	// answers "no" for an identity that is not there.
	//
	// This is for the UI ONLY — hiding the CTA, refusing the GET of the create page. It is NOT the
	// enforcement and must never be mistaken for it: between its answer and a later INSERT there is a
	// window, and a double-click is wide enough to drive through it (eight simultaneous posts from an
	// account with an allowance of 1 produced eight workspaces, every single time). The enforcement is
	// ClaimAdminSlotAsync, where the count and the insert are one statement.
	public async Task<bool> CanCreateAsync(long userId, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var quota = await db.Users
			.Where(u => u.Id == userId)
			.Select(u => (int?)u.WorkspaceQuota)
			.FirstOrDefaultAsync(ct);
		if (quota is not { } q || q <= 0) return false;

		return q > await OwnedWorkspaces(db, userId).CountAsync(ct);
	}

	// Create a workspace and hand it to its creator, or say why not.
	//
	// bypassQuota is the sysadmin free-pass (a sysadmin may create without limit — the quota is not
	// their leash). Every other caller passes false and is checked against the account's number.
	public async Task<Result> CreateAsync(
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

		using var db = dbf.Open();

		if (await db.Workspaces.AnyAsync(w => w.Key == key, ct))
			return Result.Fail($"Workspace '{key}' already exists.");

		if (!bypassQuota && creatorUserId is null)
			return Result.Fail("Only a signed-in account can create a workspace.");

		// THE SLOT IS CLAIMED FIRST, BEFORE THE WORKSPACE EXISTS. That inversion is the fix, and it is
		// why the membership row is no longer written last.
		//
		// The quota counts Admin rows in WorkspaceMembers, so the only write that can be gated on that
		// count without a window is a write to WorkspaceMembers itself: SQLite holds the write lock for
		// the whole of a single statement, so an INSERT..SELECT..WHERE (count) < (quota) sees a count
		// that already includes every slot claimed by a request that beat it here. Gating the
		// *Workspaces* insert on the same count would look every bit as atomic and would enforce
		// nothing — the membership row it counts is not written until later, so N racers would each
		// still read 0 and each still win.
		var claimed = 0;
		if (creatorUserId is { } creator)
		{
			claimed = await ClaimAdminSlotAsync(db, creator, key, bypassQuota, ct);

			// Nothing claimed, with a quota clause in play → the quota refused. (It also absorbs the
			// degenerate "already a member of a key that has no workspace": that row already counts
			// against the allowance, so refusing is the honest answer.) Under bypassQuota there is no
			// clause that can refuse — 0 there can only mean the sysadmin is already a member, so go on.
			if (claimed == 0 && !bypassQuota)
				return Result.Fail(QuotaExhaustedMessage);
		}

		try
		{
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
		}
		catch
		{
			// The slot was claimed, but the workspace it was claimed for never came to exist (a racing
			// create took the key, the container failed, …). Hand it back: an allowance quietly eaten by
			// a workspace that does not exist is worse than the failure that caused it, because nobody
			// short of a sysadmin can give it back. Releases only a row THIS call inserted.
			if (claimed > 0 && creatorUserId is { } owner)
			{
				await db.WorkspaceMembers
					.Where(m => m.UserId == owner && m.WorkspaceKey == key)
					.DeleteAsync(ct);
			}

			throw;
		}

		return Result.Success();
	}

	// Claim one workspace slot for this account, atomically: the quota check IS the insert, so there
	// is no instant between "may I?" and "done" for a second request to slip into.
	//
	// It reads as a query and it compiles to `INSERT INTO WorkspaceMembers (…) SELECT … FROM Users
	// WHERE Id = @uid AND NOT EXISTS (…) AND (SELECT COUNT(*) …) < Users.WorkspaceQuota` — one
	// statement. The Users row is the SOURCE of the insert rather than a separate lookup, which is
	// what lets the account's quota be compared inside the same statement that consumes it; it also
	// means an unknown account selects no rows and therefore claims nothing, which is the right answer
	// for an identity that is not there.
	//
	// Returns rows affected: 1 = the slot is theirs, 0 = refused.
	//
	// spec workspace-creator-is-admin: the row this writes IS the creator's Admin membership, so the
	// claim and the grant are one act. No re-login is needed for it to take effect —
	// WorkspaceClaimsRefresher rebuilds the membership claims from the DB on every request.
	//
	// No transaction, deliberately: core.db runs Cache=Shared and the SQLITE_LOCKED it raises is not
	// retried by the busy handler. A single self-contained statement needs no transaction to be atomic
	// — which is precisely why the condition had to move inside it rather than into a `if` above it.
	static Task<int> ClaimAdminSlotAsync(
		PetBoxDb db,
		long userId,
		string workspaceKey,
		bool bypassQuota,
		CancellationToken ct)
	{
		var source = db.Users
			.Where(u => u.Id == userId)
			.Where(u => !db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == workspaceKey));

		// A sysadmin is not bound by the quota (it is not their leash), so their claim carries no
		// clause — but it still goes through this one statement, so there is one way to become a
		// workspace's admin and not two.
		if (!bypassQuota)
			source = source.Where(u => OwnedWorkspaces(db, userId).Count() < u.WorkspaceQuota);

		return source.InsertAsync(
			db.WorkspaceMembers,
			u => new WorkspaceMember
			{
				UserId = userId,
				WorkspaceKey = workspaceKey,
				Role = WorkspaceRole.Admin,
			},
			ct);
	}
}
