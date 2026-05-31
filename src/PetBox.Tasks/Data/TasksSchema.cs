using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// Lazy schema bootstrap for a per-board SQLite file. Passed to
// ScopedDbFactory<TasksDb> as the ensure-schema delegate; idempotent.
//
// Applies the Core invariants (WAL + busy_timeout) then runs the Tasks-tier
// FluentMigrator migration set against this board's file. The actual DDL lives in
// Migrations/ (M001_PlanNodes) so schema changes are versioned, not hand-edited.
public static class TasksSchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString);
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_PlanNodes).Assembly);
	}
}
