using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Plans move from one-file-per-board to one-file-per-project: a project's boards now
// share a single plan_nodes table, partitioned by a Board column. Two boards each run an
// independent per-board version cursor, so (Key, Version) collides across boards — the
// PRIMARY KEY must become (Board, Key, Version), and active-key uniqueness must be
// per-board. SQLite can't alter a PK or PK columns in place, so rebuild the table (same
// technique as M004), defaulting existing rows' Board to '' (a fresh per-project file is
// empty here; the one-time data migrator stamps Board as it copies rows in from the legacy
// per-board files). Forward-only.
[Migration(5, "plan_nodes: add Board, repoint PK to (Board, Key, Version), per-board indexes")]
public sealed class M005_BoardColumn : Migration
{
	// Separate statements (not one batch): each schema change must commit before the next
	// is prepared, or SQLite resolves columns against a stale schema mid-batch.
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE plan_nodes_new (
				Board      TEXT    NOT NULL DEFAULT '',
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
				PRIMARY KEY (Board, Key, Version)
			);
			""");
		Execute.Sql("""
			INSERT INTO plan_nodes_new (Board,Key,Version,Status,Name,Body,CommitRef,Priority,PrevKey,ActiveFrom,ActiveTo,Created,Updated,Type,NodeId)
			SELECT '', Key, Version, Status, Name, Body, CommitRef, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId
			FROM plan_nodes;
			""");
		Execute.Sql("DROP TABLE plan_nodes;");
		Execute.Sql("ALTER TABLE plan_nodes_new RENAME TO plan_nodes;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_plan_nodes_active ON plan_nodes (Board, ActiveTo, Priority, Key);");
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_plan_nodes_active_board_key ON plan_nodes (Board, Key) WHERE ActiveTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
