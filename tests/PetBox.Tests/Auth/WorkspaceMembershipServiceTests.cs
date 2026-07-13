using System.Threading;
using LinqToDB;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// IWorkspaceMembershipService is the ONE door to WorkspaceMembers — every reader and every writer,
// including the two Core ones (AdminBootstrapper's seed, WorkspaceProvisioning's atomic quota claim).
// These cover the rules that are welded into it: the last admin is never orphaned, the quota is
// spent by the INSERT itself, and a cascade takes the ledger rows with it.
public sealed class WorkspaceMembershipServiceTests
{
	static (WorkspaceMembershipService Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new WorkspaceMembershipService(dbf), dbf);
	}

	static long SeedUser(ICoreDbFactory dbf, string name, int quota)
	{
		using var db = dbf.Open();
		return db.InsertWithInt64Identity(new User
		{
			Username = name,
			PasswordHash = "x",
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = quota,
		});
	}

	static void SeedWorkspace(ICoreDbFactory dbf, string key)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = key, Name = key, Description = "", CreatedAt = DateTime.UtcNow });
	}

	[Fact]
	public async Task ClaimAdminSlot_grants_admin_and_spends_the_allowance()
	{
		var (svc, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 2);
		SeedWorkspace(dbf, "alpha");

		(await svc.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: false)).Should().Be(1);

		(await svc.IsAdminAsync(uid, "alpha")).Should().BeTrue("the claim IS the creator's Admin grant");
		(await svc.CountOwnedWorkspacesAsync(uid)).Should().Be(1);
	}

	// The regression that the whole one-statement design exists to prevent: eight simultaneous claims
	// from an account with an allowance of ONE must produce exactly one membership. If the count ever
	// moves out of the INSERT and into an `if` above it, every one of these wins.
	[Fact]
	public async Task ClaimAdminSlot_is_atomic_under_parallel_claims()
	{
		var (svc, dbf) = New();
		var uid = SeedUser(dbf, "racer", quota: 1);

		const int Parallelism = 8;
		using var barrier = new Barrier(Parallelism);
		var claims = await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(i => Task.Run(async () =>
		{
			barrier.SignalAndWait();
			return await svc.ClaimAdminSlotAsync(uid, $"ws-{i}", bypassQuota: false);
		})));

		claims.Sum().Should().Be(1, "an allowance of 1 may be spent exactly once, however many requests race for it");
		(await svc.CountOwnedWorkspacesAsync(uid)).Should().Be(1);
	}

	[Fact]
	public async Task ClaimAdminSlot_bypassQuota_ignores_the_allowance()
	{
		var (svc, dbf) = New();
		var uid = SeedUser(dbf, "root", quota: 0);

		(await svc.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: true)).Should().Be(1,
			"a sysadmin is not bound by the quota — but still goes through the one statement");
		(await svc.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: true)).Should().Be(0,
			"already a member: the claim is idempotent, never a duplicate row");
	}

	[Fact]
	public async Task ClaimAdminSlot_refuses_an_exhausted_allowance()
	{
		var (svc, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);

		(await svc.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: false)).Should().Be(1);
		(await svc.ClaimAdminSlotAsync(uid, "beta", bypassQuota: false)).Should().Be(0);
	}

	[Fact]
	public async Task ReleaseSlot_hands_back_the_only_admin_row_that_RemoveMember_would_refuse()
	{
		var (svc, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);
		await svc.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: false);

		(await svc.RemoveMemberAsync("alpha", uid)).Should().Be(MemberChangeOutcome.LastAdmin,
			"the guarded path must never orphan a live workspace");

		(await svc.ReleaseSlotAsync(uid, "alpha")).Should().Be(1,
			"the compensating write is unguarded — the workspace it claimed for never came to exist");
		(await svc.CountOwnedWorkspacesAsync(uid)).Should().Be(0, "the allowance is refunded, not eaten");
	}

	[Fact]
	public async Task CountOwnedWorkspaces_excludes_the_seeded_system_workspace()
	{
		var (svc, dbf) = New();
		var uid = SeedUser(dbf, "root", quota: 5);
		SeedWorkspace(dbf, "alpha");

		using (var db = dbf.Open())
			await db.SeedMemberAsync(uid, "$system", WorkspaceRole.Admin);
		await svc.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: false);

		(await svc.CountOwnedWorkspacesAsync(uid)).Should().Be(1,
			"being a sysadmin is not a workspace the account spent allowance on");
	}

	[Fact]
	public async Task RemoveWorkspace_and_RemoveUser_drop_the_ledger_rows()
	{
		var (svc, dbf) = New();
		var a = SeedUser(dbf, "alice", quota: 3);
		var b = SeedUser(dbf, "bob", quota: 3);
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");

		await svc.ClaimAdminSlotAsync(a, "alpha", bypassQuota: false);
		await svc.ClaimAdminSlotAsync(b, "alpha", bypassQuota: false);
		await svc.ClaimAdminSlotAsync(a, "beta", bypassQuota: false);

		(await svc.RemoveWorkspaceAsync("alpha")).Should().Be(2);
		(await svc.CountMembersAsync("alpha")).Should().Be(0);
		(await svc.CountOwnedWorkspacesAsync(a)).Should().Be(1, "beta survives — and so does the allowance it spent");

		(await svc.RemoveUserAsync(a)).Should().Be(1);
		(await svc.GetRolesAsync(a)).Should().BeEmpty();
	}

	[Fact]
	public async Task ListAll_returns_every_membership_row()
	{
		var (svc, dbf) = New();
		var a = SeedUser(dbf, "alice", quota: 2);
		var b = SeedUser(dbf, "bob", quota: 2);
		SeedWorkspace(dbf, "alpha");
		await svc.ClaimAdminSlotAsync(a, "alpha", bypassQuota: false);
		(await svc.AddMemberAsync("alpha", "bob", null, WorkspaceRole.Member)).Should().Be(AddMemberOutcome.Added);

		var all = await svc.ListAllAsync();

		all.Should().BeEquivalentTo(new[]
		{
			new WorkspaceMemberOf(a, "alpha", WorkspaceRole.Admin),
			new WorkspaceMemberOf(b, "alpha", WorkspaceRole.Member),
		});
	}
}
