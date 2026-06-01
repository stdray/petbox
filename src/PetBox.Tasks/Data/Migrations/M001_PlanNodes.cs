using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Per-board temporal plan-node table. Baseline migration: uses IF NOT EXISTS so it
// adopts pre-existing files that were created by the old hand-DDL TasksSchema.Ensure
// (those have no VersionInfo table; FluentMigrator will record version 1 after).
//
// Adds the partial unique index that the hand-DDL lacked: at most one active
// revision (ActiveTo IS NULL) per Key. This turns the concurrent-insert race
// (critic C1) into a catchable constraint violation instead of a silent
// double-active row.
[Migration(1, "Create plan_nodes temporal table + unique-active-key index")]
public sealed class M001_PlanNodes : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE TABLE IF NOT EXISTS plan_nodes (
			Key        TEXT    NOT NULL,
			Version    INTEGER NOT NULL,
			Status     TEXT    NOT NULL DEFAULT '',
			Name       TEXT    NOT NULL DEFAULT '',
			Body       TEXT    NOT NULL,
			CommitRef  TEXT,
			Priority   INTEGER NOT NULL DEFAULT 0,
			PrevKey    TEXT,
			ActiveFrom INTEGER NOT NULL,
			ActiveTo   INTEGER,
			Created    TEXT    NOT NULL,
			Updated    TEXT    NOT NULL,
			PRIMARY KEY (Key, Version)
		);
		CREATE INDEX IF NOT EXISTS ix_plan_nodes_active ON plan_nodes (ActiveTo, Priority, Key);
		CREATE UNIQUE INDEX IF NOT EXISTS ux_plan_nodes_active_key ON plan_nodes (Key) WHERE ActiveTo IS NULL;
		""");

	public override void Down() => Execute.Sql("DROP TABLE IF EXISTS plan_nodes;");
}
