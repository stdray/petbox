using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Soft-delete marker for sessions: a deleted session keeps its row (DeletedAt for audit) but
// disappears from every read. A re-push of the same SessionId resurrects it — the upsert path
// replaces the whole row (last-write-wins), so a live session can't be lost to a stray delete;
// junk sessions stay deleted because nothing pushes them again.
//
// Plain typed ADD COLUMN: the flat `sessions` table is M002's, and VersionInfo guarantees M002 ran.
[Migration(3, "Add soft-delete columns to sessions")]
public sealed class M003_SessionSoftDelete : Migration
{
	public override void Up()
	{
		Alter.Table("sessions")
			.AddColumn("IsDeleted").AsInt64().NotNullable().WithDefaultValue(0)
			.AddColumn("DeletedAt").AsString().Nullable();
	}

	// SQLite's ALTER TABLE DROP COLUMN needs 3.35+; the columns are harmless to leave behind.
	public override void Down()
	{
	}
}
