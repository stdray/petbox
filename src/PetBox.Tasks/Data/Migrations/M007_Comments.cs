using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Node comments: a temporal (SCD-2) comment tree under any plan node, plus open tags.
// Two tables in the per-project tasks file (next to plan_nodes):
//   comments     — temporal comment rows; ParentId (self-ref to Key) gives the tree. At most one
//                  active revision (ActiveTo IS NULL) per Key.
//   comment_tag  — SCD-2 OPEN tags bound to a comment's Key (no tag_vocab FK, unlike node_tag —
//                  comment tags are free-form, e.g. `artifact:<slug>`).
// Additive: brand-new tables, no rebuild of plan_nodes, no backfill. Forward-only.
//
// Typed DDL throughout, except the PARTIAL indexes (the active-only lookups, and the unique
// active-key index that is the temporal invariant) — those have no typed form and go through the
// named, guarded SqliteDdl.PartialIndex.
[Migration(7, "comments temporal table + comment_tag (open tags)")]
public sealed class M007_Comments : SqliteMigration
{
	public override void Up()
	{
		Create.Table("comments")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("NodeId").AsString().NotNullable()
			.WithColumn("ParentId").AsString().Nullable()
			.WithColumn("Author").AsString().NotNullable().WithDefaultValue("")
			.WithColumn("Body").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		Create.Index("ix_comments_active").OnTable("comments")
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Board").Ascending()
			.OnColumn("NodeId").Ascending();

		// At most ONE active revision per comment Key — the temporal invariant.
		SqliteDdl.PartialIndex("ux_comments_active_key", "comments", ["Key"], "ActiveTo IS NULL", unique: true);
		SqliteDdl.PartialIndex("ix_comments_parent", "comments", ["ParentId"], "ActiveTo IS NULL");

		Create.Table("comment_tag")
			.WithColumn("CommentId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("Tag").AsString().NotNullable().PrimaryKey()
			.WithColumn("ValidFrom").AsString().NotNullable().PrimaryKey()
			.WithColumn("ValidTo").AsString().Nullable();

		SqliteDdl.PartialIndex("ix_comment_tag_comment", "comment_tag", ["CommentId"], "ValidTo IS NULL");
		SqliteDdl.PartialIndex("ix_comment_tag_board", "comment_tag", ["Board", "Tag"], "ValidTo IS NULL");
	}

	public override void Down() { } // forward-only
}
