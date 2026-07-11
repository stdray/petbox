using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// The Class-B worker's durable cursor/dead-letter state for the sessions tier — the tables
// SqliteIndexCursorStore reads and writes. The sessions file hosts no search index of its own
// (no search_fts/search_vec here); it carries only the per-session cursor SessionFactsJob parks
// its autocapture progress in, co-located with the sessions it enriches so one backup/restore
// of the file carries the progress with it. Memory (M006) and Tasks (M009) already define these
// two tables in their own tiers; the sessions tier never did — the job created them at RUNTIME
// (SqliteIndexCursorStore.EnsureSchema, now deleted). This migration is that DDL's only home.
//
// WHY THE Schema.Table(...).Exists() GUARD IS LEGITIMATE HERE — read before copying it:
// this is ADOPTION of tables that the runtime already created behind VersionInfo's back. On a
// live deployment every sessions/{project}.db where the job has run ALREADY has search_cursor
// and search_deadletter, while VersionInfo knows nothing about them (they were never a
// migration). A bare Create.Table would blow up with "table already exists" on exactly those
// files. The guard lets the migration take ownership of the pre-existing tables — their shape
// is byte-for-byte what this migration creates, and their rows (parked cursors) are preserved,
// so an adopted file and a fresh file converge on the same schema.
// This is NOT a license for "CREATE TABLE IF NOT EXISTS everywhere": the guard is a one-off
// pardon for legacy runtime-created tables, not the way new schema is written. New tables get a
// plain Create.Table.
[Migration(7, "Adopt/create the search worker's cursor + dead-letter tables in the sessions tier")]
public sealed class M007_SearchCursorTables : Migration
{
	public override void Up()
	{
		// `.Nullable()` on a PRIMARY KEY column looks wrong and is deliberate. A non-INTEGER
		// `PRIMARY KEY` column in SQLite does NOT imply NOT NULL, so the tables the runtime
		// created — and the same two tables in the memory (M006) and tasks (M009) tiers — have a
		// nullable IndexName. Declaring NOT NULL here would give a FRESH sessions file a different
		// shape from an ADOPTED one (and from the other tiers) forever. Tightening it is a
		// deliberate migration of its own, across all three tiers, not a side effect of this one.
		if (!Schema.Table("search_cursor").Exists())
			Create.Table("search_cursor")
				.WithColumn("IndexName").AsString().Nullable().PrimaryKey()
				.WithColumn("Version").AsInt64().NotNullable();

		if (!Schema.Table("search_deadletter").Exists())
			Create.Table("search_deadletter")
				.WithColumn("IndexName").AsString().NotNullable().PrimaryKey()
				.WithColumn("Type").AsString().NotNullable().PrimaryKey()
				.WithColumn("Id").AsString().NotNullable().PrimaryKey()
				.WithColumn("Attempts").AsInt32().NotNullable()
				.WithColumn("Dead").AsBoolean().NotNullable();
	}

	public override void Down()
	{
		Delete.Table("search_deadletter");
		Delete.Table("search_cursor");
	}
}
