using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// commits[] replaces the single CommitRef column (node-commits-impl). A feature is usually
// several commits, so a node's commits become an SCD-2 set in a new plan_node_commits table
// (mirroring node_tag: NodeId + Board + a value + ValidFrom/ValidTo, active while ValidTo is
// null), attached to the node's stable NodeId so they survive renames. Indexes: on Sha for
// the reverse lookup (find nodes carrying a commit), on NodeId for the per-node read.
//
// Same migration, three moves — order matters:
//   1. create plan_node_commits;
//   2. seed it from the existing non-null CommitRef values (each active node's commit becomes
//      one active row, ValidFrom = the node's Created so the seeded timestamp round-trips);
//   3. rebuild plan_nodes WITHOUT the CommitRef column (SQLite can't DROP COLUMN under old
//      versions robustly — the M004/M005 table-rebuild precedent).
// Separate Execute.Sql statements: each schema change commits before the next is prepared
// (same constraint as M004/M005). Forward-only.
[Migration(11, "plan_node_commits (SCD-2) + seed from CommitRef + drop plan_nodes.CommitRef")]
public sealed class M011_PlanNodeCommits : Migration
{
	public override void Up()
	{
		// 1. the new temporal edge table.
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS plan_node_commits (
				NodeId    TEXT NOT NULL,
				Board     TEXT NOT NULL,
				Sha       TEXT NOT NULL,
				ValidFrom TEXT NOT NULL,
				ValidTo   TEXT,
				PRIMARY KEY (NodeId, Sha, ValidFrom)
			);
			""");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_plan_node_commits_sha  ON plan_node_commits (Sha)    WHERE ValidTo IS NULL;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_plan_node_commits_node ON plan_node_commits (NodeId) WHERE ValidTo IS NULL;");

		// 2. seed from the active rows' non-empty CommitRef (normalized: trimmed + lowercased).
		//    Only active rows (ActiveTo IS NULL) with a stable NodeId carry a live commit.
		Execute.Sql("""
			INSERT OR IGNORE INTO plan_node_commits (NodeId, Board, Sha, ValidFrom)
			SELECT NodeId, Board, lower(trim(CommitRef)), Created
			FROM plan_nodes
			WHERE ActiveTo IS NULL
			  AND NodeId IS NOT NULL AND NodeId <> ''
			  AND CommitRef IS NOT NULL AND trim(CommitRef) <> '';
			""");

		// 3. rebuild plan_nodes without CommitRef (table-rebuild — M004/M005 precedent).
		Execute.Sql("""
			CREATE TABLE plan_nodes_new (
				Board      TEXT    NOT NULL DEFAULT '',
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Status     TEXT    NOT NULL DEFAULT '',
				Name       TEXT    NOT NULL DEFAULT '',
				Body       TEXT    NOT NULL,
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
			INSERT INTO plan_nodes_new (Board,Key,Version,Status,Name,Body,Priority,PrevKey,ActiveFrom,ActiveTo,Created,Updated,Type,NodeId)
			SELECT Board, Key, Version, Status, Name, Body, Priority, PrevKey, ActiveFrom, ActiveTo, Created, Updated, Type, NodeId
			FROM plan_nodes;
			""");
		Execute.Sql("DROP TABLE plan_nodes;");
		Execute.Sql("ALTER TABLE plan_nodes_new RENAME TO plan_nodes;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_plan_nodes_active ON plan_nodes (Board, ActiveTo, Priority, Key);");
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_plan_nodes_active_board_key ON plan_nodes (Board, Key) WHERE ActiveTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
