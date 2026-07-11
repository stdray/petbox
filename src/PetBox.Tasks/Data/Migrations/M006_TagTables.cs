using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Enforced tags for plan nodes (spec-flat-tags). Two tables in the per-project tasks file (next to
// plan_nodes — the FK and any later FTS denorm both need same-file):
//   tag_vocab  — the controlled vocabulary; a tag is "namespace:value", lowercased.
//   node_tag   — SCD-2 edges binding a node's stable NodeId to a tag (ValidTo null = active),
//                mirroring the Relation soft-close. Tags attach to identity, not to a content
//                revision, so they survive node edits/renames.
// The FK node_tag.Tag -> tag_vocab.Tag enforces the vocabulary in the DB (it needs the connection's
// PRAGMA foreign_keys=ON — TasksDb appends `Foreign Keys=True` to every connection string).
// Forward-only.
//
// THE FK IS INLINE IN CREATE TABLE, BY NECESSITY: SQLite has no ALTER TABLE ADD CONSTRAINT, so a
// separate Create.ForeignKey() would be silently dropped by the SQLite generator. The column-level
// .ForeignKey(...) below is emitted INSIDE the CREATE TABLE statement — the same shape M014 uses
// for relations.
//
// A NOTE ON THE FK'S NAME. The raw DDL this replaces wrote an ANONYMOUS column-level reference
// (`Tag TEXT NOT NULL REFERENCES tag_vocab(Tag)`); the typed API always emits a NAMED constraint
// (`CONSTRAINT "fk_node_tag_tag" FOREIGN KEY ...`). That difference is confined to the text in
// sqlite_master: PRAGMA foreign_key_list — which is what the golden snapshot is built from, and
// what SQLite itself enforces against — reports no constraint name at all, and SQLite never names
// a constraint in its error messages either ("FOREIGN KEY constraint failed", always). Same parent,
// same child column, same NO ACTION referential actions, same enforcement: the constraint is
// equivalent, and the golden confirms it byte-for-byte.
//
// The three tag lookups are PARTIAL indexes (only the ACTIVE edges are worth indexing) — no typed
// form, so they go through the named, guarded SqliteDdl.PartialIndex.
[Migration(6, "tag_vocab + node_tag (enforced node tags, SCD-2)")]
public sealed class M006_TagTables : SqliteMigration
{
	public override void Up()
	{
		Create.Table("tag_vocab")
			.WithColumn("Tag").AsString().NotNullable().PrimaryKey()
			.WithColumn("Namespace").AsString().NotNullable()
			.WithColumn("Description").AsString().Nullable()
			.WithColumn("CreatedAt").AsString().NotNullable();

		Create.Table("node_tag")
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("Tag").AsString().NotNullable().PrimaryKey()
				.ForeignKey("fk_node_tag_tag", "tag_vocab", "Tag")
			.WithColumn("ValidFrom").AsString().NotNullable().PrimaryKey()
			.WithColumn("ValidTo").AsString().Nullable();

		SqliteDdl.PartialIndex("ix_node_tag_tag", "node_tag", ["Tag"], "ValidTo IS NULL");
		SqliteDdl.PartialIndex("ix_node_tag_node", "node_tag", ["NodeId"], "ValidTo IS NULL");
		SqliteDdl.PartialIndex("ix_node_tag_board", "node_tag", ["Board", "Tag"], "ValidTo IS NULL");
	}

	public override void Down() { } // forward-only
}
