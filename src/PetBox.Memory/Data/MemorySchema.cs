using PetBox.Core.Data;

namespace PetBox.Memory.Data;

// Lazy schema bootstrap for a per-PROJECT SQLite file (every store of the project lives in it,
// partitioned by Store). Passed to ScopedDbFactory<MemoryDb> as the ensure-schema delegate;
// idempotent. The migration set also folds in any legacy per-store files (M010).
//
// Applies the Core invariants (WAL + busy_timeout) then runs the Memory-tier
// FluentMigrator migration set against this store's file. DDL lives in Migrations/.
public static class MemorySchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString);
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_MemoryEntries).Assembly);
	}
}
