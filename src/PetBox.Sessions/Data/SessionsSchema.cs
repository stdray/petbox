using PetBox.Core.Data;

namespace PetBox.Sessions.Data;

// Lazy schema bootstrap for a per-project sessions file; idempotent.
//
// Applies the Core invariants (WAL + busy_timeout) then runs the Sessions-tier
// FluentMigrator migration set against this project's file. DDL lives in Migrations/.
public static class SessionsSchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString);
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_Sessions).Assembly);
	}
}
