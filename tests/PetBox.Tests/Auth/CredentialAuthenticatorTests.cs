using System.Security.Claims;
using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Auth;

// The two NARROW auth doors (work db-out-of-pages-remaining-24): the credential lookup the anonymous
// login page and the REST login path share, and the self-service password change every logged-in user
// reaches. Neither may be IUserAdminService — that one resets ANY account's password — so what these
// tests pin is mostly what the doors CANNOT do.
public sealed class CredentialAuthenticatorTests
{
	// "test123", hashed. The same fixture hash LoginAuthTests uses.
	const string Password = "test123";
	const string PasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	static (CredentialAuthenticator Auth, AccountSelfService Self, IWorkspaceMembershipService Members, ICoreDbFactory Dbf) New(
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
		return (new CredentialAuthenticator(dbf, options, members), new AccountSelfService(dbf), members, dbf);
	}

	static long SeedUser(ICoreDbFactory dbf, string username, string password = Password)
	{
		using var db = dbf.Open();
		return db.InsertWithInt64Identity(new User
		{
			Username = username,
			PasswordHash = AdminPasswordHasher.Hash(password),
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = 0,
		});
	}

	static void SeedMembership(ICoreDbFactory dbf, long userId, string workspaceKey, WorkspaceRole role)
	{
		using var db = dbf.Open();
		db.Insert(new WorkspaceMember { UserId = userId, WorkspaceKey = workspaceKey, Role = role });
	}

	static ClaimsPrincipal PrincipalFor(long userId) =>
		new(new ClaimsIdentity(
			[new Claim(PetBoxClaims.UserId, userId.ToString(System.Globalization.CultureInfo.InvariantCulture))],
			"TestCookie"));

	static string? HashOf(ICoreDbFactory dbf, long userId)
	{
		using var db = dbf.Open();
		return db.Users.Where(u => u.Id == userId).Select(u => u.PasswordHash).FirstOrDefault();
	}

	// ── The credential door ──────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task A_good_password_authenticates_and_carries_the_memberships_the_login_needs()
	{
		var (auth, _, _, dbf) = New();
		var uid = SeedUser(dbf, "alice");
		SeedMembership(dbf, uid, "alpha", WorkspaceRole.Member);

		var user = (await auth.AuthenticateAsync("alice", Password))
			.Should().BeOfType<CredentialResult.Authenticated>().Which.User;

		user.Id.Should().Be(uid);
		user.Username.Should().Be("alice");
		user.IsBootstrapAdmin.Should().BeFalse();
		user.Memberships.Should().ContainSingle().Which.WorkspaceKey.Should().Be("alpha");
	}

	[Fact]
	public async Task A_wrong_password_and_a_nonexistent_user_are_the_SAME_answer()
	{
		var (auth, _, _, dbf) = New();
		SeedUser(dbf, "alice");

		var wrongPassword = (await auth.AuthenticateAsync("alice", "not-the-password"))
			.Should().BeOfType<CredentialResult.Rejected>().Which;
		var noSuchUser = (await auth.AuthenticateAsync("nobody", Password))
			.Should().BeOfType<CredentialResult.Rejected>().Which;

		wrongPassword.Reason.Should().Be(noSuchUser.Reason,
			"two different messages would make the login form an account-enumeration oracle");
		wrongPassword.Reason.Should().Be("Invalid username or password.");
	}

	[Fact]
	public async Task Empty_credentials_are_refused_without_touching_a_row()
	{
		var (auth, _, _, dbf) = New();
		SeedUser(dbf, "alice");

		(await auth.AuthenticateAsync(null, null)).Should().BeOfType<CredentialResult.Rejected>();
		(await auth.AuthenticateAsync("alice", "")).Should().BeOfType<CredentialResult.Rejected>();
		(await auth.AuthenticateAsync("", Password)).Should().BeOfType<CredentialResult.Rejected>();
	}

	// The bootstrap-admin lockdown (WS3) — the rule that moved OFF the login page and into the door,
	// so the REST login path cannot forget it.
	[Fact]
	public async Task The_bootstrap_admin_signs_in_while_it_is_the_only_system_admin()
	{
		var (auth, _, _, dbf) = New(bootstrapAdmin: "admin");
		var adminId = SeedUser(dbf, "admin");
		SeedMembership(dbf, adminId, WorkspaceMemory.SystemWorkspace, WorkspaceRole.Admin);

		var user = (await auth.AuthenticateAsync("admin", Password))
			.Should().BeOfType<CredentialResult.Authenticated>().Which.User;
		user.IsBootstrapAdmin.Should().BeTrue("the sysadmin claim is minted off this flag");
	}

	[Fact]
	public async Task The_bootstrap_admin_is_locked_out_once_another_system_admin_exists()
	{
		var (auth, _, _, dbf) = New(bootstrapAdmin: "admin");
		var adminId = SeedUser(dbf, "admin");
		SeedMembership(dbf, adminId, WorkspaceMemory.SystemWorkspace, WorkspaceRole.Admin);
		var realId = SeedUser(dbf, "real-admin");
		SeedMembership(dbf, realId, WorkspaceMemory.SystemWorkspace, WorkspaceRole.Admin);

		(await auth.AuthenticateAsync("admin", Password))
			.Should().BeOfType<CredentialResult.Rejected>()
			.Which.Reason.Should().Contain("bootstrap admin account is disabled");

		// ...and the account that locked it out still signs in.
		(await auth.AuthenticateAsync("real-admin", Password))
			.Should().BeOfType<CredentialResult.Authenticated>();
	}

	// ── The self-service door ────────────────────────────────────────────────────────────────────

	[Fact]
	public async Task A_user_changes_their_own_password_and_the_old_one_stops_working()
	{
		var (auth, self, _, dbf) = New();
		var uid = SeedUser(dbf, "alice");

		(await self.ChangeOwnPasswordAsync(PrincipalFor(uid), Password, "new-password-1"))
			.Should().BeOfType<PasswordChangeResult.Changed>();

		(await auth.AuthenticateAsync("alice", Password)).Should().BeOfType<CredentialResult.Rejected>();
		(await auth.AuthenticateAsync("alice", "new-password-1")).Should().BeOfType<CredentialResult.Authenticated>();
	}

	[Fact]
	public async Task The_wrong_current_password_changes_nothing()
	{
		var (_, self, _, dbf) = New();
		var uid = SeedUser(dbf, "alice");
		var before = HashOf(dbf, uid);

		(await self.ChangeOwnPasswordAsync(PrincipalFor(uid), "not-the-password", "new-password-1"))
			.Should().BeOfType<PasswordChangeResult.Refused>()
			.Which.Reason.Should().Contain("Current password is incorrect");

		HashOf(dbf, uid).Should().Be(before);
	}

	[Fact]
	public async Task A_short_new_password_and_an_unauthenticated_principal_are_refused()
	{
		var (_, self, _, dbf) = New();
		var uid = SeedUser(dbf, "alice");
		var before = HashOf(dbf, uid);

		(await self.ChangeOwnPasswordAsync(PrincipalFor(uid), Password, "short"))
			.Should().BeOfType<PasswordChangeResult.Refused>()
			.Which.Reason.Should().Contain("at least 8");

		// No identity at all, and an identity with no user-id claim: neither may write anything.
		(await self.ChangeOwnPasswordAsync(new ClaimsPrincipal(new ClaimsIdentity()), Password, "new-password-1"))
			.Should().BeOfType<PasswordChangeResult.Refused>();
		(await self.ChangeOwnPasswordAsync(
				new ClaimsPrincipal(new ClaimsIdentity([new Claim("name", "alice")], "TestCookie")),
				Password, "new-password-1"))
			.Should().BeOfType<PasswordChangeResult.Refused>();

		HashOf(dbf, uid).Should().Be(before);
	}

	// THE privilege test. The self-service door takes a PRINCIPAL, never a user id or a username, so
	// "change bob's password" is not a sentence it can say: the only row it can reach is the one the
	// request was authenticated as. Bob's hash is untouched no matter what alice sends — including
	// bob's own correct current password.
	[Fact]
	public async Task The_self_service_door_cannot_reach_another_users_row()
	{
		var (auth, self, _, dbf) = New();
		var alice = SeedUser(dbf, "alice", "alice-password");
		var bob = SeedUser(dbf, "bob", "bob-password");
		var bobHash = HashOf(dbf, bob);

		// Alice, correctly authenticated as alice, changes A password. It is hers, necessarily.
		(await self.ChangeOwnPasswordAsync(PrincipalFor(alice), "alice-password", "alice-new-password"))
			.Should().BeOfType<PasswordChangeResult.Changed>();

		HashOf(dbf, bob).Should().Be(bobHash, "the door has no parameter that could have named bob");
		(await auth.AuthenticateAsync("bob", "bob-password")).Should().BeOfType<CredentialResult.Authenticated>();
		(await auth.AuthenticateAsync("alice", "alice-new-password")).Should().BeOfType<CredentialResult.Authenticated>();
	}
}
