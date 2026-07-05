using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public static class AdminBootstrapper
{
	// SQLite's own SQLITE_CONSTRAINT error code — raised when an INSERT hits a UNIQUE index.
	const int SqliteConstraintViolation = 19;

	public static void EnsureAdminUser(PetBoxDb db, IOptions<AdminOptions> options)
	{
		var admin = options.Value;
		if (string.IsNullOrWhiteSpace(admin.Username) || string.IsNullOrWhiteSpace(admin.PasswordHash))
			return;

		// Seed the env-admin account only on first boot — i.e. while no $system administrator
		// exists yet. Once you've created your own admin, we never re-create or refresh the
		// env-admin account, and the Login handler refuses its credentials (PETBOX_ADMIN_FORCE
		// re-enables login for recovery). This mirrors yobaconf's bootstrap-then-lockdown.
		//
		// Cheap fast-path check outside any transaction: the common case (every boot after the
		// first) never needs to touch the write lock at all.
		if (HasSystemAdmin(db))
			return;

		// Concurrent first-boot calls (e.g. two processes racing the same fresh DB file) can both
		// pass the check above before either has written anything. The check-then-insert is made
		// safe by pairing it with DB-level uniqueness rather than trusting the re-check alone:
		// Users.Username is UNIQUE (M008) and WorkspaceMembers now has a UNIQUE (UserId,
		// WorkspaceKey) index (M035). SQLite serializes writers, so whichever caller's transaction
		// commits first "wins"; the other's commit — re-checked below, then re-attempted — collides
		// with the unique constraint and is swallowed as a no-op: the winner already produced
		// exactly the User + WorkspaceMember rows we were about to create.
		using var tx = db.BeginTransaction();
		try
		{
			if (HasSystemAdmin(db))
			{
				tx.Rollback();
				return;
			}

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

			tx.Commit();
		}
		catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
		{
			// A concurrent first-boot bootstrap won the race and already committed the admin
			// user + membership (its whole insert pair is one transaction, so if we lost we
			// necessarily lost to a fully-committed pair, not a half-written one) — nothing left
			// for us to do.
			tx.Rollback();
		}
	}

	static bool HasSystemAdmin(PetBoxDb db) =>
		db.WorkspaceMembers.Any(m => m.WorkspaceKey == "$system" && m.Role == WorkspaceRole.Admin);
}
