using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Persistent cache of per-message embeddings for the episodic index: embedding a session
// is paid ONCE, not on every re-hydration of a cold session. Keyed by (SessionId, ordinal);
// Hash invalidates a row when a re-push changed that ordinal's content, Model/Dim guard
// comparability after an embedder swap. Rows are a cache, not state — safe to wipe.
[Migration(4, "Per-message embedding cache (message_vec)")]
public sealed class M004_MessageVectors : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			CREATE TABLE IF NOT EXISTS message_vec (
				SessionId TEXT NOT NULL,
				Version INTEGER NOT NULL,
				Hash TEXT NOT NULL,
				Model TEXT NOT NULL,
				Dim INTEGER NOT NULL,
				Vec BLOB NOT NULL,
				PRIMARY KEY (SessionId, Version)
			);
			""");
	}

	public override void Down()
	{
		Execute.Sql("DROP TABLE IF EXISTS message_vec;");
	}
}
