using PetBox.Core.Data;

namespace PetBox.Config.Data;

// Lazy schema bootstrap for a per-workspace config SQLite file (config/{workspace}.db). Passed to
// ScopedDbFactory<ConfigDb> as the ensure-schema delegate; runs once per file on first open;
// idempotent.
//
// Runs the Config-tier FluentMigrator set against this workspace's file — the tier's schema is
// versioned like every other tier's (its own VersionInfo, per file). DDL lives in Migrations/.
// The hand-written `CREATE TABLE IF NOT EXISTS` + AddColumnIfMissing bootstrap this used to be is
// gone: it left no version marker and could not tell "schema absent" from "schema drifted".
// M001_ConfigBaseline ADOPTS the files that bootstrap created (see the guards there).
public static class ConfigSchema
{
	public static void Ensure(string connectionString) =>
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_ConfigBaseline).Assembly);
}
