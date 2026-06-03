using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Adds the Metadata column: a free-form structured value (JSON string) the
// mem0-compatible adapter uses to round-trip arbitrary key/values. Opaque to the
// service and NOT FTS-indexed (mem0 metadata is for equality filters, not
// full-text). Existing rows backfill to empty string. Separate from M001 to keep
// M001 the exact legacy shape (mirrors M002/M003).
[Migration(4, "Add Metadata column to memory_entries")]
public sealed class M004_MemoryMetadata : Migration
{
	public override void Up() =>
		Execute.Sql("ALTER TABLE memory_entries ADD COLUMN Metadata TEXT NOT NULL DEFAULT '';");

	public override void Down() =>
		Execute.Sql("ALTER TABLE memory_entries DROP COLUMN Metadata;");
}
