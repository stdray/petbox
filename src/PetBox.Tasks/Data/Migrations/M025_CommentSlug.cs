using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// comments.Slug — the optional human-readable address of a comment WITHIN ITS OWNING NODE
// (work `comment-slug-and-refs`, spec `comment-addressable`). Nullable, and null for every
// pre-existing comment: absence is a normal, permanent state, not a backfill gap. A comment stays
// addressable by its Key (a GUID) whether or not it has one.
//
// Typed ALTER TABLE ADD COLUMN (the M002/M021 precedent — SQLite supports it, it is expressible in
// the typed API, so no table rebuild and no raw SQL is warranted). Only the lookup index has no
// typed form and goes through the named, guarded SqliteDdl.PartialIndex.
//
// The index is deliberately NOT UNIQUE, although uniqueness within the node is the rule. It is
// enforced in CommentService.UpsertAsync, which already reads the node's active comments and can
// therefore REFUSE a duplicate through conflicts[] with a reason naming the comment that holds the
// slug. A unique index would turn the same situation into a raw SQLite constraint exception inside
// the temporal transaction — an unnamed 500 rather than a conflict — and would also have to survive
// the close-then-insert ordering of a revision write, where the retiring revision still carries the
// slug it is handing over. So the index exists for the READ (resolve a slug within a node) and the
// invariant is stated where it can be explained.
//
// Numbered 25 — the next free number after M024 in this tier (M015 is a burned number, never
// reused; see M021's note).
[Migration(25, "comments.Slug — optional per-node human-readable comment address (comment-slug-and-refs)")]
public sealed class M025_CommentSlug : SqliteMigration
{
	public override void Up()
	{
		Alter.Table("comments").AddColumn("Slug").AsString().Nullable();

		// Resolution lookup: "the active comment of THIS node carrying THIS slug". NodeId leads
		// because the uniqueness scope — and every query — is the owning node.
		SqliteDdl.PartialIndex("ix_comments_slug", "comments", ["NodeId", "Slug"], "ActiveTo IS NULL AND Slug IS NOT NULL");
	}

	public override void Down() { } // forward-only
}
