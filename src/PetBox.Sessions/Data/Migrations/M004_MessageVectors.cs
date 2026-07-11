using FluentMigrator;

namespace PetBox.Sessions.Data.Migrations;

// Persistent cache of per-message embeddings for the episodic index: embedding a session
// is paid ONCE, not on every re-hydration of a cold session. Keyed by (SessionId, ordinal);
// Hash invalidates a row when a re-push changed that ordinal's content, Model/Dim guard
// comparability after an embedder swap. Rows are a cache, not state — safe to wipe.
//
// Fully typed DDL, composite PK spelled as two `.PrimaryKey()` columns. Both PK columns were
// explicitly NOT NULL in the original raw DDL, which is also FluentMigrator's default for a PK
// column — so the shape on disk is unchanged (see M005 for the case where it is NOT).
[Migration(4, "Per-message embedding cache (message_vec)")]
public sealed class M004_MessageVectors : Migration
{
	public override void Up()
	{
		Create.Table("message_vec")
			.WithColumn("SessionId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Hash").AsString().NotNullable()
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();
	}

	// No `IF EXISTS`: Up() created the table, so Down() finds it.
	public override void Down() => Delete.Table("message_vec");
}
