using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Sessions.Data.Migrations;

// Verbatim per-session term index (spec: session-discovery-verbatim): ONE fts5 row per
// session holding the FULL stemmed token stream of its content (not just the LLM digest,
// which can drop distinctive terms the model judged non-essential). session_term_cursor
// tracks the last indexed session Version so a maintenance pass only re-tokenizes a
// session whose content actually grew — old sessions backfill on the first pass (a
// missing cursor row defaults to 0, below any real Version). Mirrors the shape of
// PetBox.Core.Search.SqliteFtsIndex (fts5 + snowball shadow terms) but keyed by SessionId
// alone: this file is already per-project scoped, unlike the entity-addressed contract
// tables (Scope/Type/Id).
//
// The cursor table is typed DDL; the FTS5 virtual table has no typed form and is SQLite-specific,
// so it goes through SqliteDdl.Fts5Table — guarded, so it cannot silently no-op on another engine.
[Migration(5, "Verbatim per-session term FTS index (session_term_fts/session_term_cursor)")]
public sealed class M005_SessionTermIndex : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "session_term_fts",
			columns: ["SessionId", "Text"],
			unindexed: ["SessionId"], // the address: stored, not tokenised
			tokenize: "unicode61");

		Create.Table("session_term_cursor")
			// `.Nullable()` on a PRIMARY KEY column looks wrong and is deliberate: the original DDL
			// said `SessionId TEXT PRIMARY KEY`, and SQLite implies NOT NULL only for an INTEGER
			// primary key — so every sessions file in production has this column NULLABLE.
			// FluentMigrator's default for a PK column is NOT NULL, which would be a (harmless
			// looking, but real) schema change; this migration must keep reproducing the shape that
			// is on disk. Tightening it is a separate, deliberate migration, not a refactor's side
			// effect. sessions.schema.txt pins it: `COL SessionId TEXT NULL PK1`.
			.WithColumn("SessionId").AsString().Nullable().PrimaryKey()
			.WithColumn("Cursor").AsInt64().NotNullable();
	}

	// No `IF EXISTS`: Up() created both, so Down() finds both.
	public override void Down()
	{
		Delete.Table("session_term_fts");
		Delete.Table("session_term_cursor");
	}
}
