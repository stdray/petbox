using LinqToDB;
using PetBox.Core.Auth;
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
//
// Every WorkspaceMembers read and write here goes through IWorkspaceMembershipService — including the
// atomic quota claim. It used to have its OWN copy of that INSERT..SELECT statement (and of the
// "which rows count as owned" predicate) alongside the service's: two copies of the one rule the
// whole quota rests on, free to drift the moment either is touched. There is now one.
public sealed class WorkspaceProvisioning(ICoreDbFactory dbf, IWorkspaceMembershipService members)
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

	// How many workspaces a user has already created. The count lives in IWorkspaceMembershipService
	// (the memberships ARE the ledger — ownership is the Admin role, "$system" excluded), and this is
	// a pass-through so that the number a caller reads and the number the claim ENFORCES are produced
	// by one expression and cannot disagree.
	public Task<int> CountOwnedWorkspacesAsync(long userId, CancellationToken ct = default) =>
		members.CountOwnedWorkspacesAsync(userId, ct);

	// True when this account may create ONE more workspace: its explicit quota still exceeds the
	// number it already owns. A missing user is false (not an exception) — an authorization question
	// answers "no" for an identity that is not there.
	//
	// This is for the UI ONLY — hiding the CTA, refusing the GET of the create page. It is NOT the
	// enforcement and must never be mistaken for it: between its answer and a later INSERT there is a
	// window, and a double-click is wide enough to drive through it (eight simultaneous posts from an
	// account with an allowance of 1 produced eight workspaces, every single time). The enforcement is
	// IWorkspaceMembershipService.ClaimAdminSlotAsync, where the count and the insert are one statement.
	public async Task<bool> CanCreateAsync(long userId, CancellationToken ct = default)
	{
		int? quota;
		using (var db = dbf.Open())
		{
			quota = await db.Users
				.Where(u => u.Id == userId)
				.Select(u => (int?)u.WorkspaceQuota)
				.FirstOrDefaultAsync(ct);
		}

		if (quota is not { } q || q <= 0) return false;

		return q > await members.CountOwnedWorkspacesAsync(userId, ct);
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
		// still read 0 and each still win. The statement itself is the membership service's (one copy,
		// see its comment); it runs on its OWN connection, which is safe precisely because no
		// transaction is held here — core.db runs Cache=Shared and an SQLITE_LOCKED under one is not
		// retried.
		var claimed = 0;
		if (creatorUserId is { } creator)
		{
			claimed = await members.ClaimAdminSlotAsync(creator, key, bypassQuota, ct);

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
			// short of a sysadmin can give it back. Releases only a row THIS call inserted — and it goes
			// through the membership service, the one door every membership writer uses.
			if (claimed > 0 && creatorUserId is { } owner)
				await members.ReleaseSlotAsync(owner, key, ct);

			throw;
		}

		return Result.Success();
	}
}
