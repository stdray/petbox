using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Node comments: a temporal (SCD-2) comment tree under any plan node, plus open tags.
// Two tables in the per-project tasks file (next to plan_nodes):
//   comments     — temporal comment rows; ParentId (self-ref to Key) gives the tree.
//                  At most one active revision (ActiveTo IS NULL) per Key.
//   comment_tag  — SCD-2 OPEN tags bound to a comment's Key (no tag_vocab FK, unlike
//                  node_tag — comment tags are free-form, e.g. `artifact:<slug>`).
// Additive: brand-new tables, no rebuild of plan_nodes, no backfill. Separate Execute.Sql
// per statement (SQLite resolves columns against the committed schema; same constraint as
// M005/M006). Forward-only.
[Migration(7, "comments temporal table + comment_tag (open tags)")]
public sealed class M007_Comments : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS comments (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Board      TEXT    NOT NULL,
				NodeId     TEXT    NOT NULL,
				ParentId   TEXT,
				Author     TEXT    NOT NULL DEFAULT '',
				Body       TEXT    NOT NULL,
				PrevKey    TEXT,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			""");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_comments_active ON comments (ActiveTo, Board, NodeId);");
		Execute.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_comments_active_key ON comments (Key) WHERE ActiveTo IS NULL;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_comments_parent ON comments (ParentId) WHERE ActiveTo IS NULL;");

		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS comment_tag (
				CommentId TEXT NOT NULL,
				Board     TEXT NOT NULL,
				Tag       TEXT NOT NULL,
				ValidFrom TEXT NOT NULL,
				ValidTo   TEXT,
				PRIMARY KEY (CommentId, Tag, ValidFrom)
			);
			""");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_comment_tag_comment ON comment_tag (CommentId)  WHERE ValidTo IS NULL;");
		Execute.Sql("CREATE INDEX IF NOT EXISTS ix_comment_tag_board   ON comment_tag (Board, Tag) WHERE ValidTo IS NULL;");
	}

	public override void Down() { } // forward-only
}
