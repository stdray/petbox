using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Vector store for semantic memory search: one packed float32 embedding per active
// entry, keyed by entry Key, tagged with the producing model + dim so the query path can
// fuse only same-(model,dim) candidates. No back-fill — rows are written lazily on the
// next upsert of each entry (embed-on-write); until then that entry is lexical-only.
// Whole-assembly FluentMigrator scan applies this to every memory store file, like M003.
//
// M006 later replaces this table with the contract-shaped search_vec.
[Migration(5, "Create memory_vec for semantic search")]
public sealed class M005_MemoryVec : Migration
{
	public override void Up() =>
		Create.Table("memory_vec")
			// `.Nullable()` on a PK column is deliberate: the original DDL said `Key TEXT PRIMARY
			// KEY`, and SQLite does NOT imply NOT NULL on a non-INTEGER primary key. FluentMigrator
			// would default a PK column to NOT NULL — a real (if benign) shape change. Keep the
			// migration reproducing exactly what it always produced. (M006 drops this table.)
			.WithColumn("Key").AsString().Nullable().PrimaryKey()
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();

	// No `IF EXISTS`: Up() created memory_vec.
	public override void Down() => Delete.Table("memory_vec");
}
