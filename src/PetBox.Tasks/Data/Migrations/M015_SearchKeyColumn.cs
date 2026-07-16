using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// search-key-column-everywhere: search_fts gets a dedicated INDEXED `Key` column for the node's
// own business key/slug. Before this, the slug reached the index only as the entity address
// (Id, UNINDEXED) or — since 8e39e398 (search-slug-words-gap) — spliced into the front of `Text`
// (`n.Key + "\n" + n.Name + "\n" + n.Body`). The splice worked but double-counted the key's words
// into the prose term frequencies and skewed BM25; TasksSearchDocs.ToDoc drops it in the SAME
// slice that adds this column (the key now lands in its OWN column instead).
//
// FTS5 does NOT support `ALTER TABLE ... ADD COLUMN` on a virtual table (verified empirically
// against the SQLite build this repo ships: "virtual tables may not be altered") — the table has
// to be REBUILT. A blind DROP+CREATE (the shape the old M015_SlugInLexicalText migration took,
// removed by reindex-as-first-class-mechanism because it raced M010's legacy-store copy) is NOT
// needed here: this migration copies every existing row across (Key defaults to '' for them) via
// the same create-new/copy/DROP/RENAME idiom M009_StoreColumn and M011_PlanNodeCommits already use
// for a table-shape change, so nothing rides between M010 (which runs first — lower migration
// number, same Up() batch) and this one gets silently dropped on the floor.
//
// This migration is DDL only — it makes the column exist. Content correctness (Key populated,
// the splice's old double-counted Text reprojected clean) is the version-gated lexical backfill's
// job (TasksSearchDocs.LexicalProjectionVersion, bumped alongside this migration in the same
// commit): a stale marker rebuilds every indexable node/comment from plan_nodes/comments on the
// next search, exactly like any other ToDoc shape change.
[Migration(15, "search_fts: add the indexed Key column (search-key-column-everywhere)")]
public sealed class M015_SearchKeyColumn : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "search_fts_new",
			columns: ["Scope", "Type", "Id", "Text", "Tags", "Key"],
			unindexed: ["Scope", "Type", "Id"], // the entity address: stored, not tokenised — same as before
			tokenize: "unicode61");

		SqliteDdl.Raw(
			"table rebuild: carry every existing search_fts row across verbatim (an INSERT..SELECT has " +
			"no typed form for a virtual table) — Key defaults to '' for pre-existing rows; the " +
			"version-gated lexical backfill (TasksSearchDocs.LexicalProjectionVersion) reprojects it on " +
			"the next search, so no row is ever left with a stale empty Key",
			"""
			INSERT INTO search_fts_new (Scope, Type, Id, Text, Tags, Key)
			SELECT Scope, Type, Id, Text, Tags, '' FROM search_fts;
			""");

		Delete.Table("search_fts");
		Rename.Table("search_fts_new").To("search_fts");
	}

	public override void Down() { } // forward-only
}
