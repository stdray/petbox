using FluentMigrator;

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
[Migration(5, "Verbatim per-session term FTS index (session_term_fts/session_term_cursor)")]
public sealed class M005_SessionTermIndex : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE VIRTUAL TABLE IF NOT EXISTS session_term_fts USING fts5(
			SessionId UNINDEXED, Text, tokenize='unicode61'
		);
		CREATE TABLE IF NOT EXISTS session_term_cursor (
			SessionId TEXT PRIMARY KEY, Cursor INTEGER NOT NULL
		);
		""");

	public override void Down() => Execute.Sql("""
		DROP TABLE IF EXISTS session_term_fts;
		DROP TABLE IF EXISTS session_term_cursor;
		""");
}
