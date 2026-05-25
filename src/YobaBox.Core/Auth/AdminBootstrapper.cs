using LinqToDB;
using Microsoft.Extensions.Options;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Core.Auth;

public static class AdminBootstrapper
{
	public static void EnsureAdminUser(YobaBoxDb db, IOptions<AdminOptions> options)
	{
		var admin = options.Value;
		if (string.IsNullOrWhiteSpace(admin.Username) || string.IsNullOrWhiteSpace(admin.PasswordHash))
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

		var hasSystem = db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == "$system");
		if (!hasSystem)
		{
			db.Insert(new WorkspaceMember
			{
				UserId = userId,
				WorkspaceKey = "$system",
				Role = WorkspaceRole.Admin,
			});
		}
	}
}
