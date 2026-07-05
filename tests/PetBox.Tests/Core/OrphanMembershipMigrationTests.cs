using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M032_CleanOrphanMemberships backfill: WorkspaceMember rows whose workspace no longer exists
// (orphaned by the pre-fix delete-workspace path) are removed when the migration runs, while
// memberships of live workspaces — including the seeded $system — survive.
//
// Staged migration test: migrate to v31, hand-insert one orphan + one valid membership, then
// migrate up to v32 and assert. (Runs the real FluentMigrator runner rather than the cached
// TestSchema template, which is already fully migrated and so can't observe a pre-32 state.)
public sealed class OrphanMembershipMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public OrphanMembershipMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m032-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		TestDirs.CleanupOrDefer(_dir);
	}

	ServiceProvider BuildRunner() =>
		new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(_cs)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider();

	static void Exec(SqliteConnection conn, string sql)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	static long ScalarLong(SqliteConnection conn, string sql)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		return Convert.ToInt64(cmd.ExecuteScalar());
	}

	[Fact]
	public void Migration_removes_orphan_memberships_keeps_valid_ones()
	{
		// 1. Migrate the schema up to just before the cleanup migration.
		using (var sp = BuildRunner())
		{
			using var scope = sp.CreateScope();
			scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(31);
		}

		// 2. Seed a live workspace + a membership in it, plus an orphan membership whose
		//    workspace was never created. $system membership stands in for the seeded ws.
		using (var conn = new SqliteConnection(_cs))
		{
			conn.Open();
			Exec(conn, "INSERT INTO Workspaces (Key, Name, Description, CreatedAt) VALUES ('keep', 'Keep', '', datetime('now'))");
			Exec(conn, "INSERT INTO Users (Username, PasswordHash, CreatedAt) VALUES ('admin', 'x', datetime('now'))");
			Exec(conn, "INSERT INTO WorkspaceMembers (UserId, WorkspaceKey, Role) VALUES (1, 'keep', 0)");
			Exec(conn, "INSERT INTO WorkspaceMembers (UserId, WorkspaceKey, Role) VALUES (1, '$system', 0)");
			Exec(conn, "INSERT INTO WorkspaceMembers (UserId, WorkspaceKey, Role) VALUES (1, 'ghost', 0)"); // orphan
			ScalarLong(conn, "SELECT COUNT(*) FROM WorkspaceMembers").Should().Be(3);
		}

		// 3. Apply the remaining migrations (M032 runs here).
		using (var sp = BuildRunner())
		{
			using var scope = sp.CreateScope();
			scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
		}

		// 4. Only the orphan is gone.
		using (var conn = new SqliteConnection(_cs))
		{
			conn.Open();
			ScalarLong(conn, "SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceKey = 'ghost'").Should().Be(0);
			ScalarLong(conn, "SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceKey = 'keep'").Should().Be(1);
			ScalarLong(conn, "SELECT COUNT(*) FROM WorkspaceMembers WHERE WorkspaceKey = '$system'").Should().Be(1);
		}
	}
}
