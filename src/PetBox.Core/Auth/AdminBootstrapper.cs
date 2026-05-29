using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public static class AdminBootstrapper
{
	public static void EnsureAdminUser(PetBoxDb db, IOptions<AdminOptions> options)
	{
		var admin = options.Value;
		if (string.IsNullOrWhiteSpace(admin.Username) || string.IsNullOrWhiteSpace(admin.PasswordHash))
			return;

		// Seed the env-admin account only on first boot — i.e. while no $system administrator
		// exists yet. Once you've created your own admin, we never re-create or refresh the
		// env-admin account, and the Login handler refuses its credentials (PETBOX_ADMIN_FORCE
		// re-enables login for recovery). This mirrors yobaconf's bootstrap-then-lockdown.
		var anySystemAdmin = db.WorkspaceMembers.Any(m => m.WorkspaceKey == "$system" && m.Role == WorkspaceRole.Admin);
		if (anySystemAdmin)
			return;

		var existing = db.Users.FirstOrDefault(u => u.Username == admin.Username);
		long userId;
		if (existing is null)
		{
			userId = (long)db.InsertWithInt64Identity(new User
			{
				Username = admin.Username,
				PasswordHash = admin.PasswordHash,
				CreatedAt = DateTime.UtcNow,
			});
		}
		else
		{
			userId = existing.Id;
			if (string.IsNullOrEmpty(existing.PasswordHash))
			{
				db.Users
					.Where(u => u.Id == userId)
					.Set(u => u.PasswordHash, admin.PasswordHash)
					.Update();
			}
		}

		db.Insert(new WorkspaceMember
		{
			UserId = userId,
			WorkspaceKey = "$system",
			Role = WorkspaceRole.Admin,
		});
	}
}
