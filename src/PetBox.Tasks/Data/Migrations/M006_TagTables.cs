using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Enforced tags for plan nodes (spec-flat-tags). Two tables in the per-project tasks
// file (next to plan_nodes — the FK and any later FTS denorm both need same-file):
//   tag_vocab  — the controlled vocabulary; a tag is "namespace:value", lowercased.
//   node_tag   — SCD-2 edges binding a node's stable NodeId to a tag (ValidTo null =
//                active), mirroring the Relation soft-close. Tags attach to identity,
//                not to a content revision, so they survive node edits/renames.
// The FK node_tag.Tag -> tag_vocab.Tag enforces the vocabulary in the DB (requires the
// connection's PRAGMA foreign_keys=ON — set via TasksDb connection string). Separate
// Execute.Sql statements: each schema change commits before the next is prepared
// (same constraint as M005). Forward-only.
[Migration(6, "tag_vocab + node_tag (enforced node tags, SCD-2)")]
public sealed class M006_TagTables : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS tag_vocab (
				Tag         TEXT NOT NULL PRIMARY KEY,
				Namespace   TEXT NOT NULL,
				Description TEXT,
				CreatedAt   TEXT NOT NULL
			);
			""");
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS node_tag (
				NodeId    TEXT NOT NULL,
				Board     TEXT NOT NULL,
				Tag       TEXT NOT NULL REFERENCES tag_vocab(Tag),
				ValidFrom TEXT NOT NULL,
				ValidTo   TEXT,
				PRIMARY KEY (NodeId, Tag, ValidFrom)
			);
			""");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_node_tag_tag   ON node_tag (Tag)        WHERE ValidTo IS NULL;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_node_tag_node  ON node_tag (NodeId)     WHERE ValidTo IS NULL;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_node_tag_board ON node_tag (Board, Tag) WHERE ValidTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
