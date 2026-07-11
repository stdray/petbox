using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Memory.Data.Migrations;

// Retrofit memory search behind the PetBox.Core.Search contract. Replaces the bespoke
// memory_fts / memory_vec (keyed only by entry Key, since the file is already per-store) with
// the contract's entity-addressed tables (Scope, Type, Id): search_fts (Class-A lexical floor,
// written INSIDE the entity tx) + search_vec (Class-B vectors, dim 1024, materialized by the
// async-vectorization worker) + the worker's durable cursor/dead-letter state. DDL mirrors
// SqliteFtsIndex/VectorSearchIndex/SqliteIndexCursorStore.EnsureSchema. Whole-assembly scan
// applies it to every memory store file. Lexical content is rebuilt cheaply on first search;
// vectors are re-embedded by the worker (cursor starts at 0 = full backfill).
//
// Everything here is typed DDL except the FTS5 virtual table, which has no typed form and is
// SQLite-specific (SqliteDdl.Fts5Table — guarded, so it cannot silently no-op on another engine).
//
// The DROPs carry no `IF EXISTS`: memory_fts and memory_vec are created by M003 and M005, which
// VersionInfo guarantees ran before this one. If they are somehow absent, that is schema drift
// and this migration SHOULD fail loudly instead of shrugging.
[Migration(6, "Replace memory_fts/memory_vec with contract search tables (search_fts/vec/cursor/deadletter)")]
public sealed class M006_SearchTables : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "search_fts",
			columns: ["Scope", "Type", "Id", "Text", "Tags"],
			unindexed: ["Scope", "Type", "Id"], // the entity address: stored, not tokenised
			tokenize: "unicode61");

		Create.Table("search_vec")
			.WithColumn("Scope").AsString().NotNullable().PrimaryKey()
			.WithColumn("Type").AsString().NotNullable().PrimaryKey()
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();

		Create.Table("search_cursor")
			// `.Nullable()` on a PRIMARY KEY column looks wrong and is deliberate: the original DDL
			// said `IndexName TEXT PRIMARY KEY`, and SQLite does NOT imply NOT NULL on a
			// non-INTEGER primary key — so every memory file in production has this column
			// nullable. FluentMigrator's default for a PK column is NOT NULL, which would be a
			// (harmless-looking, but real) schema change; this migration must keep reproducing the
			// shape that is on disk. Tightening it is a separate, deliberate migration, not a
			// side effect of a refactor.
			.WithColumn("IndexName").AsString().Nullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable();

		Create.Table("search_deadletter")
			.WithColumn("IndexName").AsString().NotNullable().PrimaryKey()
			.WithColumn("Type").AsString().NotNullable().PrimaryKey()
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Attempts").AsInt32().NotNullable()
			.WithColumn("Dead").AsBoolean().NotNullable();

		Delete.Table("memory_fts"); // created by M003
		Delete.Table("memory_vec"); // created by M005
	}

	// Symmetric inverse of Up(): the four tables it created are dropped, the two it dropped are
	// recreated in their M003/M005 shape. Again no `IF EXISTS` — Down() runs only after Up().
	public override void Down()
	{
		Delete.Table("search_fts");
		Delete.Table("search_vec");
		Delete.Table("search_cursor");
		Delete.Table("search_deadletter");

		SqliteDdl.Fts5Table(
			name: "memory_fts",
			columns: ["Key", "Description", "Body", "Tags"],
			unindexed: ["Key"],
			tokenize: "unicode61");

		Create.Table("memory_vec")
			.WithColumn("Key").AsString().Nullable().PrimaryKey() // nullable PK: see M005
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();
	}
}
