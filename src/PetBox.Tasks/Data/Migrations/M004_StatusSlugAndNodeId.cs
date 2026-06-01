using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Migrates legacy per-board files (lazily, on first open under the new code — no
// central DB, no hand-run script, no per-project access). Legacy files declared
// `Status` as INTEGER (old PlanStatus enum); linq2db picks its reader from the
// DECLARED column type, so text slugs in an INTEGER column read back as 0. The only
// robust fix is to change the column type, which SQLite does via a table rebuild:
//   - rebuild plan_nodes with Status TEXT (+ Type/NodeId from M002/M003);
//   - copy ALL revisions (history preserved), remapping integer enum {0..5} → slug;
//   - give each ACTIVE node a stable NodeId.
// On a fresh file the table is empty at this point and Status is already TEXT, so the
// CASE preserves text values and the rebuild is a trivial no-op. Forward-only.
[Migration(4, "Rebuild plan_nodes: Status INTEGER->TEXT slug; backfill NodeId")]
public sealed class M004_StatusSlugAndNodeId : Migration
{
	// Separate statements (not one batch): each schema change must commit before the
	// next is prepared, or SQLite resolves columns against a stale schema mid-batch.
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE plan_nodes_new (
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
				Type       TEXT    NOT NULL DEFAULT '',
				NodeId     TEXT    NOT NULL DEFAULT '',
				PRIMARY KEY (Key, Version)
			);
			""");
		Execute.Sql("""
			INSERT INTO plan_nodes_new (Key,Version,Status,Name,Body,CommitRef,Priority,PrevKey,ActiveFrom,ActiveTo,Created,Updated,Type,NodeId)
			SELECT Key, Version,
				CASE WHEN typeof(Status) = 'integer' THEN
					CASE CAST(Status AS INTEGER)
						WHEN 0 THEN 'Pending' WHEN 1 THEN 'InProgress' WHEN 2 THEN 'Done'
						WHEN 3 THEN 'Blocked' WHEN 4 THEN 'Deferred' WHEN 5 THEN 'Cancelled' ELSE '' END
				ELSE Status END,
				Name, Body, CommitRef, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId
			FROM plan_nodes;
			""");
		Execute.Sql("DROP TABLE plan_nodes;");
		Execute.Sql("ALTER TABLE plan_nodes_new RENAME TO plan_nodes;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_plan_nodes_active ON plan_nodes (ActiveTo, Priority, Key);");
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_plan_nodes_active_key ON plan_nodes (Key) WHERE ActiveTo IS NULL;");
		Execute.Sql("UPDATE plan_nodes SET NodeId = lower(hex(randomblob(16))) WHERE ActiveTo IS NULL AND (NodeId IS NULL OR NodeId = '');");
	}

	public override void Down() { } // forward-only data migration
}
