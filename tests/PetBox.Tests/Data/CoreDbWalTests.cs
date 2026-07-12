using Microsoft.Data.Sqlite;
using PetBox.Core.Data;

namespace PetBox.Tests.Data;

// core.db must run in WAL, like every other internal tier.
//
// It was the last one in journal_mode=DELETE: Tasks/Memory/Sessions/Deploy each call
// SqlitePragmas.ApplyWal from their own *Schema.Ensure before their migration set, but core.db is
// bootstrapped by MigrationRunner.Run(cs) (Program.cs), which never applied the pragmas. Under
// DELETE a writer holds an EXCLUSIVE lock on the whole file, so a concurrent reader gets
// SQLITE_BUSY instead of WAL's pre-write snapshot — and core.db is the file whose connection count
// grows as PetBoxDb moves behind a connection factory.
//
// journal_mode is written into the DB file HEADER, so it is set once and survives every reopen —
// which is why the assertion is made on a FRESH connection, not on the one that ran the pragma.
public sealed class CoreDbWalTests
{
	static string ReadJournalMode(string connectionString)
	{
		using var conn = new SqliteConnection(connectionString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA journal_mode;";
		return ((string)cmd.ExecuteScalar()!).ToLowerInvariant();
	}

	[Fact]
	public void MigrationRunner_LeavesCoreDbInWal()
	{
		var cs = TestSchema.NewTempConnectionString();

		MigrationRunner.Run(cs);

		ReadJournalMode(cs).Should().Be("wal",
			"core.db is bootstrapped by MigrationRunner.Run and must come out of it in WAL — under "
			+ "journal_mode=DELETE a writer locks the whole file against every concurrent reader");
	}

	// The suite doesn't re-migrate per test: it migrates ONCE into a template and File.Copy's it
	// (TestSchema.Core). Only the .db is copied — never the -wal/-shm sidecars — so if the template
	// were copied with un-checkpointed WAL frames, every test would start from a db missing its most
	// recent pages. TestSchema checkpoint(TRUNCATE)s before copying; this pins that the copy is both
	// COMPLETE (the schema the migrations built is there) and still WAL (the mode rides in the header).
	[Fact]
	public void TemplateCopy_PreservesWal_AndTheMigratedSchema()
	{
		var cs = TestSchema.NewTempConnectionString();

		TestSchema.Core(cs);

		ReadJournalMode(cs).Should().Be("wal",
			"journal_mode lives in the file header, so a copied template stays WAL");

		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		// VersionInfo is FluentMigrator's own bookkeeping table: present iff MigrateUp actually ran
		// and the copy carries its pages.
		cmd.CommandText = "SELECT COUNT(*) FROM VersionInfo;";
		Convert.ToInt32(cmd.ExecuteScalar()).Should().BeGreaterThan(0,
			"the copied template must carry the migrated schema, not a checkpoint-truncated stub");
	}
}
