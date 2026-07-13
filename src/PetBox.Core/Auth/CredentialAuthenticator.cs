using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

// The identity a successful sign-in produces: everything the login path needs to build a cookie
// principal, and nothing it does not. There is NO PasswordHash on this record — the hash is read
// inside the service, compared there, and never crosses the door. A page that cannot see a hash
// cannot leak one.
public sealed record AuthenticatedUser(
	long Id,
	string Username,
	bool IsBootstrapAdmin,
	IReadOnlyList<WorkspaceMembership> Memberships);

// The answer to "are these credentials good?" — and it is an ANSWER, not an exception: a wrong
// password is the normal outcome of a login form, not an exceptional one.
//
// `Rejected.Reason` is the text the caller shows. Wrong password and nonexistent user deliberately
// carry the SAME string ("Invalid username or password.") — the login form must not be usable to
// enumerate accounts, and that is a property of this service, not of whoever renders it next.
public abstract record CredentialResult
{
	CredentialResult() { }

	public sealed record Authenticated(AuthenticatedUser User) : CredentialResult;
	public sealed record Rejected(string Reason) : CredentialResult;
}

// THE AUTHENTICATION DOOR — the credential lookup, and only the credential lookup.
//
// It exists because the anonymous login page and the REST login path both need to turn a
// (name, password) pair into an identity, and neither may be handed IUserAdminService: that service
// creates, deletes and RESETS THE PASSWORD OF any account, and it is admin-scoped for exactly that
// reason. Handing it to the page that anyone on the internet can reach would be a privilege
// widening dressed up as a db-out-of-pages conversion.
//
// The narrowness is the point, and it is structural rather than conventional: this interface has one
// method, it takes a password and returns an identity, and there is no write on it at all. It cannot
// express "give me user X's hash", it cannot express "set user X's password", and it cannot express
// "tell me whether user X exists" (both misses answer the same way). The worst a caller can do with
// it is check a password it was already given.
//
// It also owns the BOOTSTRAP-ADMIN LOCKDOWN, because that is an authentication rule and a rule that
// lives in a page is a rule the next page forgets: once the instance has a $system administrator of
// its own, the env-declared account (PETBOX_ADMIN_*) can no longer sign in unless PETBOX_ADMIN_FORCE
// re-enables it for recovery. The web login page and the REST login path get that rule by asking,
// not by re-implementing it.
public interface ICredentialAuthenticator
{
	Task<CredentialResult> AuthenticateAsync(string? username, string? password, CancellationToken ct = default);
}

public sealed class CredentialAuthenticator(
	ICoreDbFactory dbf,
	IOptions<AdminOptions> adminOptions,
	IWorkspaceMembershipService members) : ICredentialAuthenticator
{
	// One string for both misses. Do not split it: two messages is an account-enumeration oracle.
	const string BadCredentials = "Invalid username or password.";

	const string BootstrapLockedOut =
		"The bootstrap admin account is disabled because another administrator exists. Sign in with your own account.";

	public async Task<CredentialResult> AuthenticateAsync(
		string? username, string? password, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
			return new CredentialResult.Rejected("Enter a username and password.");

		User? user;
		using (var db = dbf.Open())
			user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

		if (user is null || !AdminPasswordHasher.Verify(password, user.PasswordHash))
			return new CredentialResult.Rejected(BadCredentials);

		var isBootstrapAdmin = IsBootstrapAdmin(user.Username);
		if (isBootstrapAdmin && !AdminForceEnabled() && await AnotherSysAdminExistsAsync(user.Id, ct))
			return new CredentialResult.Rejected(BootstrapLockedOut);

		// Memberships come through their own door (they are the workspace-quota ledger, and every
		// reader of them shares one implementation so a cache over them could ever be correct).
		// Order is the table's, unchanged: the caller's "active workspace" is the first membership.
		var memberships = await members.GetRolesAsync(user.Id, ct);

		return new CredentialResult.Authenticated(
			new AuthenticatedUser(user.Id, user.Username, isBootstrapAdmin, memberships));
	}

	bool IsBootstrapAdmin(string username) =>
		!string.IsNullOrEmpty(adminOptions.Value.Username)
		&& string.Equals(username, adminOptions.Value.Username, StringComparison.Ordinal);

	// "Is there a $system administrator OTHER than this account?" — the lockdown's actual question.
	// Expressed as (all $system admins) minus (this account, if it is one) so that both reads go
	// through the membership service rather than around it. No lockout risk: when the env-admin is
	// the only $system admin the count is 1, it is this account, and the difference is 0.
	async Task<bool> AnotherSysAdminExistsAsync(long userId, CancellationToken ct)
	{
		var admins = await members.CountAdminsAsync(WorkspaceMemory.SystemWorkspace, ct);
		var self = await members.IsAdminAsync(userId, WorkspaceMemory.SystemWorkspace, ct) ? 1 : 0;
		return admins - self > 0;
	}

	static bool AdminForceEnabled() =>
		string.Equals(Environment.GetEnvironmentVariable("PETBOX_ADMIN_FORCE"), "true", StringComparison.OrdinalIgnoreCase);
}
