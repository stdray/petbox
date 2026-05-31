using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Adds the Type taxonomy column (user|feedback|project|reference, stored as the
// MemoryType enum's int). Separate from M001 so M001 stays the exact legacy shape
// for adoption of pre-existing hand-DDL files. Existing rows backfill to Project
// (2) — the most common kind for already-written project notes.
[Migration(2, "Add Type taxonomy column to memory_entries")]
public sealed class M002_MemoryType : Migration
{
	public override void Up() =>
		Execute.Sql("ALTER TABLE memory_entries ADD COLUMN Type INTEGER NOT NULL DEFAULT 2;");

	public override void Down() =>
		Execute.Sql("ALTER TABLE memory_entries DROP COLUMN Type;");
}
