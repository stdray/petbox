using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// search-key-column-everywhere: search_fts gets a dedicated INDEXED `Key` column for the node's
// own business key/slug. Before this, the slug reached the index only as the entity address
// (Id, UNINDEXED) or — since search-slug-words-gap — spliced into the front of `Text`
// (`n.Key + "\n" + n.Name + "\n" + n.Body`). The splice worked but double-counted the key's words
// into the prose term frequencies and skewed BM25; TasksSearchDocs.ToDoc drops it in the SAME
// slice that adds this column (the key now lands in its OWN column instead).
//
// FTS5 does NOT support `ALTER TABLE ... ADD COLUMN` on a virtual table (verified empirically
// against the SQLite build this repo ships: "virtual tables may not be altered") — the table has
// to be REBUILT. This migration rebuilds it with the create-new/copy/DROP/RENAME idiom
// M009_StoreColumn and M011_PlanNodeCommits already use for a table-shape change, carrying every
// existing row across (Key defaults to '' for them). Losing those rows would not corrupt anything
// — search_fts is a derived mirror and the backfill re-projects an empty index on the next search
// — but copying costs one INSERT..SELECT and saves every project file that rebuild, so there is no
// reason to drop them.
//
// The paired M012_SearchKeyColumn on the MEMORY tier has a HARDER constraint, and it is the one
// that must not be copied over here by analogy: there M010_MergeLegacyStoreFiles merges legacy
// per-store rows INTO search_fts, so a rebuild that dropped rows would silently discard that
// one-time merge. This tier has no such migration — M010 here is M010_MethodologyDefs, an additive
// table that never touches search_fts.
//
// This migration is DDL only — it makes the column exist. Content correctness (Key populated,
// the splice's old double-counted Text reprojected clean) is the version-gated lexical backfill's
// job (TasksSearchDocs.LexicalProjectionVersion, bumped alongside this migration in the same
// commit): a stale marker rebuilds every indexable node/comment from plan_nodes/comments on the
// next search, exactly like any other ToDoc shape change.
// NUMBERED 16, NOT 15, AND THAT IS LOAD-BEARING. reindex-as-first-class-mechanism deleted the
// old M015_SlugInLexicalText from this folder in the immediately preceding commit, which frees
// the NUMBER but not the fact: FluentMigrator skips any version already in a file's VersionInfo
// table, so a database that ran the old M015 (any checkout taken between search-slug-words-gap
// and reindex-as-first-class-mechanism — prod never did, it stopped at 14) would silently SKIP
// a new migration numbered 15
// and never grow this column. Ensure() does not throw on that path; it surfaces later as
// "no such column: Key" on the first index write. Reproduced against such a file before the
// renumber. A deleted migration's number is BURNED — never reuse one.
[Migration(16, "search_fts: add the indexed Key column (search-key-column-everywhere)")]
public sealed class M016_SearchKeyColumn : SqliteMigration
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
