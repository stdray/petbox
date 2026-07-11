using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Memory.Data.Migrations;

// Per-store temporal memory-entry table, plus the partial unique index that keeps the temporal
// model honest: at most ONE active revision (ActiveTo IS NULL) per Key. The Type column /
// taxonomy lands in a later migration (plan A4); M009 later repartitions the table by Store.
//
// The table and the plain index are typed FluentMigrator DDL. The partial unique index is the
// one thing the typed API cannot express, so it goes through SqliteDdl.PartialIndex — named, and
// guarded to FAIL on a non-SQLite engine rather than quietly degrade into a TOTAL unique index
// (which would forbid history outright).
//
// This migration used to carry `IF NOT EXISTS`, to adopt files created by the old hand-DDL
// MemorySchema.Ensure. That is gone: a migration runs exactly once, gated by VersionInfo, so a
// tolerant CREATE never protected anything — it only stood ready to swallow a schema divergence.
// The legacy per-store files are not migrated in place any more either: M009/M010 build a fresh
// per-PROJECT file and copy their rows in.
[Migration(1, "Create memory_entries temporal table + unique-active-key index")]
public sealed class M001_MemoryEntries : SqliteMigration
{
	public override void Up()
	{
		Create.Table("memory_entries")
			.WithColumn("Key").AsString().NotNullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable().PrimaryKey()
			.WithColumn("Description").AsString().NotNullable()
			.WithColumn("Body").AsString().NotNullable()
			.WithColumn("Tags").AsString().NotNullable()
			.WithColumn("PrevKey").AsString().Nullable()
			.WithColumn("ActiveFrom").AsInt64().NotNullable()
			.WithColumn("ActiveTo").AsInt64().Nullable()
			.WithColumn("Created").AsString().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		Create.Index("ix_memory_entries_active").OnTable("memory_entries")
			.OnColumn("ActiveTo").Ascending()
			.OnColumn("Key").Ascending();

		SqliteDdl.PartialIndex(
			name: "ux_memory_entries_active_key",
			table: "memory_entries",
			columns: ["Key"],
			where: "ActiveTo IS NULL",
			unique: true);
	}

	// No `IF EXISTS`: Up() created the table, so Down() finds it (its indexes go with it).
	public override void Down() => Delete.Table("memory_entries");
}
