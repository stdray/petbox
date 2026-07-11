using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Memory.Data.Migrations;

// FTS5 search index over active memory entries (Description/Body/Tags), keyed by
// Key. Tokenised + ranked — replaces the substring scan in memory_search, so an
// agent can find a note by paraphrase/word rather than exact substring (a recall
// gap that would otherwise bias the dogfooding compliance signal).
//
// The mirror is rebuilt from the active set on every upsert (small per-store sets;
// avoids temporal-aware trigger complexity). This migration also back-fills it for
// entries that already exist in adopted files.
//
// Neither statement has a typed form and both are SQLite-specific: FTS5 is a virtual-table
// module (SqliteDdl.Fts5Table), and the back-fill is an INSERT..SELECT, which Insert.IntoTable
// cannot express — it writes literal rows only (SqliteDdl.Raw, with the reason recorded).
//
// M006 later replaces this table with the contract-shaped search_fts.
[Migration(3, "Create memory_fts FTS5 index over active entries")]
public sealed class M003_MemoryFts : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "memory_fts",
			columns: ["Key", "Description", "Body", "Tags"],
			unindexed: ["Key"],
			tokenize: "unicode61");

		SqliteDdl.Raw(
			"back-fill the FTS mirror from the active entries — an INSERT..SELECT, which the typed API cannot express (Insert.IntoTable takes literal rows only)",
			"""
			INSERT INTO memory_fts (Key, Description, Body, Tags)
				SELECT Key, Description, Body, Tags FROM memory_entries WHERE ActiveTo IS NULL;
			""");
	}

	// No `IF EXISTS`: Up() created memory_fts. DROP TABLE works on an FTS5 virtual table too — it
	// takes the shadow tables with it.
	public override void Down() => Delete.Table("memory_fts");
}
