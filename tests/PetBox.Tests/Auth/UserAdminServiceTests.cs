using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// IUserAdminService owns the three rules that entangle Users with WorkspaceMembers: the allowance is
// spent by Admin memberships, the last $system admin may never be deleted, and deleting an account
// takes its memberships (the quota ledger) with it.
public sealed class UserAdminServiceTests
{
	const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	static (UserAdminService Users, IWorkspaceMembershipService Members, ICoreDbFactory Dbf) New(
		string? bootstrapAdmin = null)
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		var members = new WorkspaceMembershipService(dbf);
		var options = Options.Create(new AdminOptions
		{
			Username = bootstrapAdmin ?? string.Empty,
			PasswordHash = bootstrapAdmin is null ? string.Empty : PasswordHash,
		});
		return (new UserAdminService(dbf, options, members), members, dbf);
	}

	static void SeedWorkspace(ICoreDbFactory dbf, string key)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = key, Name = key, Description = "", CreatedAt = DateTime.UtcNow });
	}

	static async Task<long> CreateUser(UserAdminService users, string name, int quota)
	{
		(await users.CreateAsync(name, "pw", quota)).Should().BeOfType<UserChangeResult.Changed>();
		return (await users.ListAsync()).Single(u => u.Username == name).Id;
	}

	[Fact]
	public async Task Create_demands_an_explicit_allowance()
	{
		var (users, _, _) = New();

		(await users.CreateAsync("alice", "pw", null)).Should().BeOfType<UserChangeResult.Refused>()
			.Which.Reason.Should().Contain("allowance",
				"\"nobody decided\" and \"decided: none\" are different facts — only the second may be stored");
		(await users.CreateAsync("alice", "pw", -1)).Should().BeOfType<UserChangeResult.Refused>();
		(await users.CreateAsync("alice", "", 0)).Should().BeOfType<UserChangeResult.Refused>();

		(await users.CreateAsync("alice", "pw", 2)).Should().BeOfType<UserChangeResult.Changed>();
		(await users.CreateAsync("alice", "pw", 2)).Should().BeOfType<UserChangeResult.Refused>()
			.Which.Reason.Should().Contain("already exists");
	}

	[Fact]
	public async Task List_reports_owned_workspaces_by_the_same_rule_the_quota_enforces()
	{
		var (users, members, dbf) = New();
		var uid = await CreateUser(users, "alice", quota: 3);
		SeedWorkspace(dbf, "alpha");
		await members.ClaimAdminSlotAsync(uid, "alpha", bypassQuota: false);
		using (var db = dbf.Open())
			await db.SeedMemberAsync(uid, "$system", WorkspaceRole.Admin);

		var alice = (await users.ListAsync()).Single(u => u.Username == "alice");

		alice.WorkspaceQuota.Should().Be(3);
		alice.WorkspacesOwned.Should().Be(1, "$system is not spent allowance");
		alice.Memberships.Select(m => m.WorkspaceKey).Should().Equal("$system", "alpha");
	}

	[Fact]
	public async Task SetQuota_and_ResetPassword_answer_NotFound_for_a_missing_account()
	{
		var (users, _, dbf) = New();
		var uid = await CreateUser(users, "alice", quota: 0);

		(await users.SetQuotaAsync(uid, null)).Should().BeOfType<UserChangeResult.Refused>();
		(await users.SetQuotaAsync(uid, 4)).Should().BeOfType<UserChangeResult.Changed>();
		(await users.SetQuotaAsync(9999, 4)).Should().BeOfType<UserChangeResult.NotFound>();
		(await users.GetAsync(uid))!.WorkspaceQuota.Should().Be(4);

		(await users.ResetPasswordAsync(uid, " ")).Should().BeOfType<UserChangeResult.Refused>();
		(await users.ResetPasswordAsync(uid, "fresh")).Should().BeOfType<UserChangeResult.Changed>();
		(await users.ResetPasswordAsync(9999, "fresh")).Should().BeOfType<UserChangeResult.NotFound>();

		using var db = dbf.Open();
		db.Users.Single(u => u.Id == uid).PasswordHash.Should().NotBe("fresh", "the hash is stored, never the password");
	}

	[Fact]
	public async Task Delete_refuses_self_the_bootstrap_admin_and_the_last_sysadmin()
	{
		var (users, members, dbf) = New(bootstrapAdmin: "root");
		var root = await CreateUser(users, "root", quota: 0);
		var alice = await CreateUser(users, "alice", quota: 0);
		var bob = await CreateUser(users, "bob", quota: 0);
		using (var db = dbf.Open())
			await db.SeedMemberAsync(alice, "$system", WorkspaceRole.Admin);

		(await users.DeleteAsync(alice, actingUserId: alice)).Should().BeOfType<UserChangeResult.Refused>()
			.Which.Reason.Should().Contain("your own account");

		(await users.DeleteAsync(root, actingUserId: alice)).Should().BeOfType<UserChangeResult.Refused>()
			.Which.Reason.Should().Contain("bootstrap admin");

		(await users.DeleteAsync(alice, actingUserId: bob)).Should().BeOfType<UserChangeResult.Refused>()
			.Which.Reason.Should().Contain("last system administrator");

		// A second sysadmin makes alice deletable — and her memberships must go WITH her.
		using (var db = dbf.Open())
			await db.SeedMemberAsync(bob, "$system", WorkspaceRole.Admin);

		(await users.DeleteAsync(alice, actingUserId: bob)).Should().BeOfType<UserChangeResult.Changed>();
		(await users.GetAsync(alice)).Should().BeNull();
		(await members.GetRolesAsync(alice)).Should().BeEmpty("memberships are the quota ledger — they never outlive the account");

		(await users.DeleteAsync(9999, actingUserId: bob)).Should().BeOfType<UserChangeResult.NotFound>();
	}

	[Fact]
	public async Task EnsureBootstrapAdmin_seeds_once_and_only_while_there_is_no_system_admin()
	{
		var (users, members, _) = New(bootstrapAdmin: "root");

		(await users.EnsureBootstrapAdminAsync()).Should().BeTrue();
		var root = (await users.ListAsync()).Single(u => u.Username == "root");
		(await members.IsAdminAsync(root.Id, "$system")).Should().BeTrue();
		root.IsBootstrapAdmin.Should().BeTrue();

		(await users.EnsureBootstrapAdminAsync()).Should().BeFalse(
			"once an instance has a $system admin the env account is never re-seeded");
		(await members.CountAdminsAsync("$system")).Should().Be(1);
	}

	[Fact]
	public async Task EnsureBootstrapAdmin_is_a_no_op_without_configured_credentials()
	{
		var (users, members, _) = New();

		(await users.EnsureBootstrapAdminAsync()).Should().BeFalse();
		(await members.CountAdminsAsync("$system")).Should().Be(0);
	}
}
