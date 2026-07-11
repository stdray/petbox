using FluentMigrator;
using Microsoft.Data.Sqlite;

namespace PetBox.Memory.Data.Migrations;

// The data half of the per-project merge (M009 is the schema half): copy every legacy per-store
// file `memory/{project}/{store}.db` into this project file, stamping the source file's name into
// the new Store column. See LegacyStoreMerge for the copy/verify/resume mechanics.
//
// TransactionBehavior.None is load-bearing, twice over:
//   * the merge must NOT be one all-or-nothing transaction — per-store commits are what make an
//     interrupted run RESUMABLE (3 of 5 stores done → a re-run finishes the other 2);
//   * SQLite cannot ATTACH a database inside a transaction, and Microsoft.Data.Sqlite opens one
//     with BEGIN IMMEDIATE (a write lock), which would also deadlock LegacyStoreMerge's own
//     connection against the runner's.
// So the migration owns no transaction; LegacyStoreMerge opens its own connection to this same
// file and runs one transaction PER STORE. Its progress log (memory_store_merge, written in the
// same per-store transaction) is what makes the re-run exact rather than merely safe.
//
// A fresh install has no legacy directory → this is a no-op.
[Migration(10, TransactionBehavior.None, "Merge legacy per-store memory files into the per-project file")]
public sealed class M010_MergeLegacyStoreFiles : Migration
{
	public override void Up() => Execute.WithConnection((conn, _) =>
	{
		var path = new SqliteConnectionStringBuilder(conn.ConnectionString).DataSource;
		if (!string.IsNullOrEmpty(path))
			LegacyStoreMerge.Run(path);
	});

	public override void Down() { } // forward-only: the legacy files are still on disk (see LegacyStoreMerge)
}
