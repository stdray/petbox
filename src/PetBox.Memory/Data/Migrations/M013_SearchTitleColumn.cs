using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Memory.Data.Migrations;

// search-doc-model-title-weights: search_fts gets a dedicated INDEXED `Title` column. For a memory
// entry the Description IS the title (a free port), so it moves out of the spliced `Text`
// (`Description + "\n" + Body`, as MemorySearchDocs.ToDoc did before) into its own field, letting
// the lexical leg weight a title hit above a body hit (FtsColumnWeights: Key 3 / Title 2 / Tags 2 /
// Body 1). Title is APPENDED as the last column — the same additive move M012 made for `Key` — so
// the fts5 bm25() weight vector stays positional over [Scope, Type, Id, Text, Tags, Key, Title].
//
// FTS5 does NOT support `ALTER TABLE ... ADD COLUMN` on a virtual table (verified empirically
// against the SQLite build this repo ships: "virtual tables may not be altered") — the table has
// to be REBUILT. Like M012 (and NOT the old M015-shape blind DELETE that raced
// M010_MergeLegacyStoreFiles), this copies every existing row across via the create-new/copy/DROP/
// RENAME idiom, so a fresh install's M010 legacy-merge rows survive intact; Title just defaults to
// '' for them, same as any pre-existing row.
//
// This migration is DDL only — it makes the column exist. Content correctness (Title populated, the
// old splice's `Text` reprojected to body-only) is the version-gated lexical backfill's job
// (MemorySearchDocs.LexicalProjectionVersion, bumped 2→3 in the same commit): a stale per-store
// marker rebuilds that store's search_fts from memory_entries on the next search, so a deployed
// file's existing entries land in the Title column with no entry re-saved.
[Migration(13, "search_fts: add the indexed Title column (search-doc-model-title-weights)")]
public sealed class M013_SearchTitleColumn : SqliteMigration
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
			"version-gated lexical backfill (MemorySearchDocs.LexicalProjectionVersion) reprojects it on " +
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
