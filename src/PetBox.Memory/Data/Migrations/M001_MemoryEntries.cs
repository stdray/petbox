using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Per-store temporal memory-entry table. Baseline migration: IF NOT EXISTS adopts
// pre-existing files created by the old hand-DDL MemorySchema.Ensure. Adds the
// partial unique index (one active revision per Key) that the hand-DDL lacked.
// The Type column / taxonomy lands in a later migration (plan A4).
[Migration(1, "Create memory_entries temporal table + unique-active-key index")]
public sealed class M001_MemoryEntries : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE TABLE IF NOT EXISTS memory_entries (
			Key         TEXT    NOT NULL,
			Version     INTEGER NOT NULL,
			Description TEXT    NOT NULL,
			Body        TEXT    NOT NULL,
			Tags        TEXT    NOT NULL,
			PrevKey     TEXT,
			ActiveFrom  INTEGER NOT NULL,
			ActiveTo    INTEGER,
			Created     TEXT    NOT NULL,
			Updated     TEXT    NOT NULL,
			PRIMARY KEY (Key, Version)
		);
		CREATE INDEX IF NOT EXISTS ix_memory_entries_active ON memory_entries (ActiveTo, Key);
		CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_entries_active_key ON memory_entries (Key) WHERE ActiveTo IS NULL;
		""");

	public override void Down() => Execute.Sql("DROP TABLE IF EXISTS memory_entries;");
}
