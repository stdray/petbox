using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Data.Migrations;

namespace PetBox.Tests.Data;

// The LOG tier was the last one left in journal_mode=DELETE: every other tier
// (Tasks/Memory/Sessions/Deploy, and core.db itself) applies SqlitePragmas.ApplyWal before its
// migration run, but LogSchema.Ensure called MigrationRunner.Run(cs, assembly) directly and never
// applied the pragmas. Under DELETE a writer holds an EXCLUSIVE lock on the whole file, so a
// concurrent reader gets SQLITE_BUSY instead of WAL's pre-write snapshot — and the `access` log is
// written BY being read (opening its page issues requests that get logged to the same file), which
// made its own logs page hang. Mirrors CoreDbWalTests.
//
// journal_mode is written into the DB file HEADER, so it is set once and survives every reopen —
// which is why the assertion is made on a FRESH connection, not on the one that ran the pragma.
public sealed class LogSchemaWalTests
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
	public void Ensure_LeavesFreshLogDbInWal()
	{
		var cs = TestSchema.NewTempConnectionString("petbox-log-wal");

		LogSchema.Ensure(cs);

		ReadJournalMode(cs).Should().Be("wal",
			"a freshly-created log db is bootstrapped by LogSchema.Ensure and must come out of it in "
			+ "WAL — under journal_mode=DELETE a writer locks the whole file against every concurrent "
			+ "reader, which is exactly what made the self-logged `access` log hang its own page");
	}

	// Every live log file on prod predates this fix and is sitting in DELETE mode already. Simulate
	// that by running the OLD bootstrap path (MigrationRunner.Run without the pragma, which is
	// exactly what LogSchema.Ensure used to do) to build a migrated-but-DELETE-mode file, then call
	// the FIXED Ensure again — the same sequence prod's petbox.db/access.db go through on next open
	// after deploy.
	[Fact]
	public void Ensure_MigratesExistingDeleteModeLogDbToWal()
	{
		var cs = TestSchema.NewTempConnectionString("petbox-log-wal-existing");

		MigrationRunner.Run(cs, typeof(M001_LogBaseline).Assembly);
		ReadJournalMode(cs).Should().Be("delete",
			"sanity check: without the pragma, a fresh file defaults to journal_mode=DELETE, matching "
			+ "every log db that predates this fix");

		LogSchema.Ensure(cs);

		ReadJournalMode(cs).Should().Be("wal",
			"journal_mode is persistent in the file header, so an EXISTING pre-fix log db must convert "
			+ "to WAL the first time Ensure runs after deploy, same as core.db's conversion");

		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM VersionInfo;";
		Convert.ToInt32(cmd.ExecuteScalar()).Should().BeGreaterThan(0,
			"the migrated schema (and its VersionInfo marker) must survive the WAL conversion — "
			+ "Ensure re-runs MigrateUp against the same file, it must not recreate it");
	}
}
