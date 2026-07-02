using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// FTS5 search index over active memory entries (Description/Body/Tags), keyed by
// Key. Tokenised + ranked — replaces the substring scan in memory_search, so an
// agent can find a note by paraphrase/word rather than exact substring (a recall
// gap that would otherwise bias the dogfooding compliance signal).
//
// The mirror is rebuilt from the active set on every upsert (small per-store sets;
// avoids temporal-aware trigger complexity). This migration also back-fills it for
// entries that already exist in adopted files.
[Migration(3, "Create memory_fts FTS5 index over active entries")]
public sealed class M003_MemoryFts : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
			Key UNINDEXED, Description, Body, Tags, tokenize='unicode61'
		);
		INSERT INTO memory_fts (Key, Description, Body, Tags)
			SELECT Key, Description, Body, Tags FROM memory_entries WHERE ActiveTo IS NULL;
		""");

	public override void Down() => Execute.Sql("DROP TABLE IF EXISTS memory_fts;");
}
