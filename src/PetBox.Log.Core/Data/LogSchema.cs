using PetBox.Core.Data;

namespace PetBox.Log.Core.Data;

// Lazy schema bootstrap for a per-log SQLite file (logs/{project}/{log}.db: LogEntries + Spans +
// MetricPoints). Passed to ScopedDbFactory<LogDb> as the ensure-schema delegate; runs once per
// file on first open; idempotent.
//
// Runs the Log-tier FluentMigrator set against this log's file — the tier's schema is versioned
// like every other tier's (its own VersionInfo, per file). DDL lives in Migrations/. The raw
// `CREATE TABLE IF NOT EXISTS` bootstrap this used to be is gone: it left no version marker and
// could not tell "schema absent" from "schema drifted". M001_LogBaseline ADOPTS the files that
// bootstrap created (see the guards there).
public static class LogSchema
{
	public static void Ensure(string connectionString) =>
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_LogBaseline).Assembly);
}
