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
//
// Applies the Core invariants (WAL + busy_timeout) before the migration run, like every other
// tier (Tasks/Memory/Sessions/Deploy, and core.db itself) — this was the one tier that got
// missed. Under journal_mode=DELETE a writer takes an EXCLUSIVE lock on the whole file, so a
// concurrent reader gets SQLITE_BUSY instead of the pre-write snapshot WAL would hand it; that
// bit hardest on the `access` log, which is written BY being read (opening its page issues
// requests that get logged to the same file). journal_mode lives in the file header, so an
// EXISTING file (already in DELETE mode) converts to WAL the first time it is opened after
// deploy — same as core.db's conversion.
public static class LogSchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString);
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_LogBaseline).Assembly);
	}
}
