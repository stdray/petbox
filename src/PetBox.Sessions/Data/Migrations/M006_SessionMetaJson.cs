using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Optional observed client metadata (JSON object) on a session row — last-write-wins when
// a push supplies X-PetBox-Session-Meta / MCP meta. NOT server-authoritative: the client
// stamps its local role→model binding here for observation only (binding-not-server-authoritative).
// Null/absent on a write leaves any existing value intact.
[Migration(6, "Add nullable MetaJson column to sessions")]
public sealed class M006_SessionMetaJson : Migration
{
	public override void Up() =>
		Execute.Sql("ALTER TABLE sessions ADD COLUMN MetaJson TEXT NULL;");

	// SQLite ALTER TABLE DROP COLUMN needs 3.35+; the column is harmless to leave behind.
	public override void Down()
	{
	}
}
