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
//
// Applies the Core invariants (WAL + busy_timeout) before the migration run, like every other tier
// (Tasks/Memory/Sessions/Deploy/Log, and core.db itself) — config was the last one left out, for no
// reason anyone recorded. Under journal_mode=DELETE a writer takes an EXCLUSIVE lock on the whole
// file, so a concurrent reader gets SQLITE_BUSY rather than the pre-write snapshot WAL would hand
// it. journal_mode lives in the file HEADER, so an EXISTING file (every live workspace config is in
// DELETE today) converts the first time it is opened after deploy — no migration, no data movement;
// LegacySchemaAdoptionTests drives a populated legacy file through this exact path.
//
// This also removes a live trap next door: SqliteTier.Telemetry means synchronous=NORMAL, which is
// only safe under WAL — under DELETE it risks corruption on power loss rather than merely losing
// the tail. While config sat in DELETE, retiering it would have been quietly unsafe. It no longer is.
public static class ConfigSchema
{
	public static void Ensure(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString, SqliteTier.Durable);
		MigrationRunner.Run(connectionString, typeof(Migrations.M001_ConfigBaseline).Assembly, SqliteTier.Durable);
	}
}
