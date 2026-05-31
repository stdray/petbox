using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Per-project temporal sessions table. Baseline migration: IF NOT EXISTS adopts
// pre-existing files created by the old hand-DDL SessionsSchema.Ensure. Adds the
// partial unique index (one active revision per Key) that the hand-DDL lacked.
[Migration(1, "Create sessions temporal table + unique-active-key index")]
public sealed class M001_Sessions : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE TABLE IF NOT EXISTS sessions (
			Key        TEXT    NOT NULL,
			Version    INTEGER NOT NULL,
			Agent      TEXT    NOT NULL,
			Content    TEXT    NOT NULL,
			PrevKey    TEXT,
			ActiveFrom INTEGER NOT NULL,
			ActiveTo   INTEGER,
			Created    TEXT    NOT NULL,
			Updated    TEXT    NOT NULL,
			PRIMARY KEY (Key, Version)
		);
		CREATE INDEX IF NOT EXISTS ix_sessions_active ON sessions (ActiveTo, Key);
		CREATE UNIQUE INDEX IF NOT EXISTS ux_sessions_active_key ON sessions (Key) WHERE ActiveTo IS NULL;
		""");

	public override void Down() => Execute.Sql("DROP TABLE IF EXISTS sessions;");
}
