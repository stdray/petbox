using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Memory.Data.Migrations;

// search-key-column-everywhere: search_fts gets a dedicated INDEXED `Key` column for a memory
// entry's own business key. Memory never had the tasks tier's Text-splice fix (search-slug-words-gap
// / 8e39e398 never touched MemorySearchDocs) — the key simply had NO lexical leg at all: unlike
// tasks, memory has no exact-identifier retriever either, so an English memory key never bridged
// into a Russian-titled query by any path. This column is the fix for both tiers at once, from
// the shared SqliteFtsIndex schema — MemorySearchDocs.ToDoc projects e.Key into it in the same
// slice.
//
// FTS5 does NOT support `ALTER TABLE ... ADD COLUMN` on a virtual table (verified empirically
// against the SQLite build this repo ships: "virtual tables may not be altered") — the table has
// to be REBUILT. This is NOT the old M015_SlugInLexicalText shape (a blind DELETE FROM search_fts
// to force reprojection, removed by reindex-as-first-class-mechanism specifically because it raced
// M010_MergeLegacyStoreFiles copying legacy per-store search_fts rows in): this migration copies
// every existing row across via the SAME create-new/copy/DROP/RENAME idiom M009_StoreColumn already
// uses for memory_entries/entry_usage, so a fresh install's M010 (which runs first — lower
// migration number, same Up() batch) still lands its merged rows intact; Key just defaults to ''
// for them, same as for any pre-existing row.
//
// This migration is DDL only — it makes the column exist. Content correctness (Key populated) is
// the version-gated lexical backfill's job (MemorySearchDocs.LexicalProjectionVersion, bumped
// alongside this migration in the same commit): a stale per-store marker rebuilds that store's
// search_fts from memory_entries on the next search, exactly like any other ToDoc shape change.
[Migration(12, "search_fts: add the indexed Key column (search-key-column-everywhere)")]
public sealed class M012_SearchKeyColumn : SqliteMigration
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
			"version-gated lexical backfill (MemorySearchDocs.LexicalProjectionVersion) reprojects it on " +
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
