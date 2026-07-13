using System.Globalization;
using System.Security.Claims;
using LinqToDB;
using PetBox.Core.Data;

namespace PetBox.Core.Auth;

// The outcome of a self-service account change. `Refused` carries the text the caller shows; there
// is no NotFound, on purpose — "your session names a user that no longer exists" is a refusal to the
// only person who can see it, not a 404 anybody may probe for.
public abstract record PasswordChangeResult
{
	PasswordChangeResult() { }

	public sealed record Changed : PasswordChangeResult;
	public sealed record Refused(string Reason) : PasswordChangeResult;
}

// THE SELF-SERVICE DOOR — "change MY OWN password", and nothing else.
//
// Every logged-in user reaches /Me/Security, so this door is reachable by every logged-in user. That
// is why it may not be IUserAdminService (which can reset ANY account's password: it is admin-scoped
// for that reason) and why it does not take a user id or a username.
//
// IT CANNOT EXPRESS "SOMEONE ELSE'S PASSWORD". The account is derived from the authenticated
// ClaimsPrincipal — the cookie the auth handler issued and signed — inside this method; there is no
// parameter through which a caller could name a different account, so no form field, query string or
// route value can ever reach the WHERE clause. That is a structural property, not a check somebody
// has to remember: the only user id this service will ever write to is the one the request was
// authenticated as. The current password is verified on top of that, so even a forged principal
// would still need the account's existing password.
public interface IAccountSelfService
{
	Task<PasswordChangeResult> ChangeOwnPasswordAsync(
		ClaimsPrincipal principal, string? currentPassword, string? newPassword, CancellationToken ct = default);
}

public sealed class AccountSelfService(ICoreDbFactory dbf) : IAccountSelfService
{
	public const int MinPasswordLength = 8;

	public async Task<PasswordChangeResult> ChangeOwnPasswordAsync(
		ClaimsPrincipal principal, string? currentPassword, string? newPassword, CancellationToken ct = default)
	{
		if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
			return new PasswordChangeResult.Refused("All three fields are required.");

		if (newPassword.Length < MinPasswordLength)
			return new PasswordChangeResult.Refused(
				$"New password must be at least {MinPasswordLength} characters.");

		// THE identity, and the only one this method can act on.
		if (principal.Identity is not { IsAuthenticated: true }
			|| !long.TryParse(
				principal.FindFirst(PetBoxClaims.UserId)?.Value,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out var userId))
		{
			return new PasswordChangeResult.Refused("Session is missing user id. Sign out and back in.");
		}

		using var db = dbf.Open();

		var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
		if (user is null)
			return new PasswordChangeResult.Refused("User not found.");

		if (!AdminPasswordHasher.Verify(currentPassword, user.PasswordHash))
			return new PasswordChangeResult.Refused("Current password is incorrect.");

		var newHash = AdminPasswordHasher.Hash(newPassword);
		await db.Users.Where(u => u.Id == userId).Set(u => u.PasswordHash, newHash).UpdateAsync(ct);

		return new PasswordChangeResult.Changed();
	}
}
