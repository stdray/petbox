using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Adds a `Created` timestamp — the flat `sessions` table (M002) only ever carried `Updated`
// (last-write time), so there was no way to sort/report "when did this session first appear"
// (card ui-search-sessions-hybrid: sort by updated/created/transcript-length). Existing rows
// have no real creation time to recover, so they backfill from their own Updated — an honest
// best-effort floor, not a fabricated date; every row written AFTER this migration gets its
// real first-seen timestamp (SessionService preserves it across every re-push/append, since
// UpsertAsync replaces the whole row — see GetCreatedAsync).
//
// Left NULLABLE in the DB (SQLite ADD COLUMN can't express a per-row default in one step; the
// backfill UPDATE runs right after). SessionRow still declares it NotNull — every row is
// backfilled immediately below and every future write always sets it explicitly.
[Migration(8, "Add Created column to sessions, backfilled from Updated")]
public sealed class M008_SessionCreated : Migration
{
	public override void Up()
	{
		Alter.Table("sessions").AddColumn("Created").AsString().Nullable();
		Execute.Sql("UPDATE sessions SET Created = Updated WHERE Created IS NULL;");
	}

	// SQLite's ALTER TABLE DROP COLUMN needs 3.35+; the column is harmless to leave behind.
	public override void Down()
	{
	}
}
