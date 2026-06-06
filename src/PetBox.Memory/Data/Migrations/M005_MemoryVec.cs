using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Vector store for semantic memory search: one packed float32 embedding per active
// entry, keyed by entry Key, tagged with the producing model + dim so the query path can
// fuse only same-(model,dim) candidates. No back-fill — rows are written lazily on the
// next upsert of each entry (embed-on-write); until then that entry is lexical-only.
// Whole-assembly FluentMigrator scan applies this to every memory store file, like M003.
[Migration(5, "Create memory_vec for semantic search")]
public sealed class M005_MemoryVec : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE TABLE IF NOT EXISTS memory_vec (
			Key TEXT PRIMARY KEY,
			Model TEXT NOT NULL,
			Dim INTEGER NOT NULL,
			Vec BLOB NOT NULL
		);
		""");

	public override void Down() => Execute.Sql("DROP TABLE IF EXISTS memory_vec;");
}
