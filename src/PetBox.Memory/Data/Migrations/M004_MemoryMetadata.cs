using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Adds the Metadata column: a free-form structured value (JSON string) clients use
// to round-trip arbitrary key/values. Opaque to the service and NOT FTS-indexed
// (metadata is for equality filters, not full-text). Existing rows backfill to
// empty string. Separate from M001 to keep M001 the exact legacy shape (mirrors
// M002/M003).
[Migration(4, "Add Metadata column to memory_entries")]
public sealed class M004_MemoryMetadata : Migration
{
	public override void Up() =>
		Alter.Table("memory_entries")
			.AddColumn("Metadata").AsString().NotNullable().WithDefaultValue("");

	public override void Down() =>
		Delete.Column("Metadata").FromTable("memory_entries");
}
