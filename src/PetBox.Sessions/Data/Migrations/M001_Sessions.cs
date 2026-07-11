using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Sessions.Data.Migrations;

// Per-project temporal (SCD-2) sessions table, plus the partial unique index that keeps the
// temporal model honest: at most ONE active revision (ActiveTo IS NULL) per Key.
//
// SHORT-LIVED BY DESIGN: M002 flattens this shape into a latest-snapshot table (and drops this
// one), so on a clean database nothing of M001 survives to the golden snapshot. It stays as the
// history M002 reads its rows out of.
//
// The table and the plain index are typed FluentMigrator DDL. The partial unique index is the one
// thing the typed API cannot express, so it goes through SqliteDdl.PartialIndex — named, and
// guarded to FAIL on a non-SQLite engine rather than quietly degrade into a TOTAL unique index
// (which would forbid history outright).
//
// This migration used to carry `IF NOT EXISTS`, to adopt files created by the old hand-DDL
// SessionsSchema.Ensure. That is gone: a migration runs exactly once, gated by VersionInfo, so a
// tolerant CREATE never protected anything — it only stood ready to swallow a schema divergence.
[Migration(1, "Create sessions temporal table + unique-active-key index")]
public sealed class M001_Sessions : SqliteMigration
{
	public override void Up()
	{
		Create.Table("sessions")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Agent").AsString().NotNullable()
			.WithColumn("Content").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		Create.Index("ix_sessions_active").OnTable("sessions")
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Key").Ascending();

		SqliteDdl.PartialIndex(
			name: "ux_sessions_active_key",
			table: "sessions",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	// No `IF EXISTS`: Up() created the table, so Down() finds it (its indexes go with it).
	public override void Down() => Delete.Table("sessions");
}
