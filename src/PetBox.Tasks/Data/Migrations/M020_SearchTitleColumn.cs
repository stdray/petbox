using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// search-doc-model-title-weights: search_fts gets a dedicated INDEXED `Title` column so the node's
// title (PlanNode.Name) lives in its OWN field instead of being spliced onto the front of `Text`
// (`Name + "\n" + Body`, as TasksSearchDocs.ToDoc did before this slice). A splice made the title
// indistinguishable from the body at rank time; a dedicated column lets the lexical leg weight a
// title hit above a body hit (FtsColumnWeights: Key 3 / Title 2 / Tags 2 / Body 1). Title is
// APPENDED as the last column — the same additive move M016 made for `Key` — so the fts5 bm25()
// weight vector stays positional over [Scope, Type, Id, Text, Tags, Key, Title].
//
// FTS5 does NOT support `ALTER TABLE ... ADD COLUMN` on a virtual table (verified empirically
// against the SQLite build this repo ships: "virtual tables may not be altered") — the table has
// to be REBUILT. This migration rebuilds it with the create-new/copy/DROP/RENAME idiom M016 already
// uses, carrying every existing row across (Title defaults to '' for them). Losing those rows would
// not corrupt anything — search_fts is a derived mirror and the backfill re-projects — but copying
// costs one INSERT..SELECT and saves every project file that rebuild, so there is no reason to drop.
//
// This migration is DDL only — it makes the column exist. Content correctness (Title populated, the
// old splice's `Text` reprojected to body-only) is the version-gated lexical backfill's job
// (TasksSearchDocs.LexicalProjectionVersion, bumped 3→4 in the same commit): a stale marker rebuilds
// every indexable node/comment from plan_nodes/comments on the next search, so a deployed file's
// existing content lands in the Title column with no node re-saved. Numbered 20 — the next free
// number after M019 in this tier's used-migration-numbers registry (a deleted number is BURNED,
// never reused).
[Migration(20, "search_fts: add the indexed Title column (search-doc-model-title-weights)")]
public sealed class M020_SearchTitleColumn : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "search_fts_new",
			columns: ["Scope", "Type", "Id", "Text", "Tags", "Key", "Title"],
			unindexed: ["Scope", "Type", "Id"], // the entity address: stored, not tokenised — same as before
			tokenize: "unicode61");

		SqliteDdl.Raw(
			"table rebuild: carry every existing search_fts row across verbatim (an INSERT..SELECT has " +
			"no typed form for a virtual table) — Title defaults to '' for pre-existing rows; the " +
			"version-gated lexical backfill (TasksSearchDocs.LexicalProjectionVersion) reprojects it on " +
			"the next search, so no row is ever left with a stale empty Title",
			"""
			INSERT INTO search_fts_new (Scope, Type, Id, Text, Tags, Key, Title)
			SELECT Scope, Type, Id, Text, Tags, Key, '' FROM search_fts;
			""");

		Delete.Table("search_fts");
		Rename.Table("search_fts_new").To("search_fts");
	}

	public override void Down() { } // forward-only
}
