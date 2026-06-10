using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Soft-delete marker for sessions: a deleted session keeps its row (DeletedAt for audit) but
// disappears from every read. A re-push of the same SessionId resurrects it — the upsert path
// replaces the whole row (last-write-wins), so a live session can't be lost to a stray delete;
// junk sessions stay deleted because nothing pushes them again.
[Migration(3, "Add soft-delete columns to sessions")]
public sealed class M003_SessionSoftDelete : Migration
{
	public override void Up()
	{
		Execute.Sql("ALTER TABLE sessions ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0;");
		Execute.Sql("ALTER TABLE sessions ADD COLUMN DeletedAt TEXT NULL;");
	}

	// SQLite's ALTER TABLE DROP COLUMN needs 3.35+; the columns are harmless to leave behind.
	public override void Down()
	{
	}
}
