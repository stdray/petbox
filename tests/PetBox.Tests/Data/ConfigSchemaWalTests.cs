using Microsoft.Data.Sqlite;
using PetBox.Config.Data;
using PetBox.Config.Data.Migrations;
using PetBox.Core.Data;

namespace PetBox.Tests.Data;

// CONFIG was the last tier still bootstrapping in journal_mode=DELETE: every other one
// (Tasks/Memory/Sessions/Deploy/Log, and core.db itself) applies SqlitePragmas.ApplyWal before its
// migration run, but ConfigSchema.Ensure called MigrationRunner.Run directly. Under DELETE a writer
// holds an EXCLUSIVE lock on the whole file, so a concurrent reader gets SQLITE_BUSY instead of
// WAL's pre-write snapshot. Mirrors LogSchemaWalTests and CoreDbWalTests.
//
// journal_mode is written into the DB file HEADER, so it is set once and survives every reopen —
// which is why each assertion is made on a FRESH connection, not on the one that ran the pragma.
public sealed class ConfigSchemaWalTests : IDisposable
{
	readonly string _dir;

	public ConfigSchemaWalTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-config-wal-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	string Cs(string name) => SqliteConnectionStrings.ForFile(Path.Combine(_dir, name + ".db"));

	static string ReadJournalMode(string connectionString)
	{
		using var conn = new SqliteConnection(connectionString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA journal_mode;";
		return ((string)cmd.ExecuteScalar()!).ToLowerInvariant();
	}

	static object? Scalar(string connectionString, string sql)
	{
		using var conn = new SqliteConnection(connectionString);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		return cmd.ExecuteScalar();
	}

	[Fact]
	public void Ensure_LeavesFreshConfigDbInWal()
	{
		var cs = Cs("fresh");

		ConfigSchema.Ensure(cs);

		ReadJournalMode(cs).Should().Be("wal",
			"a freshly-created workspace config db is bootstrapped by ConfigSchema.Ensure and must come "
			+ "out of it in WAL like every other tier — under journal_mode=DELETE a writer locks the whole "
			+ "file against every concurrent reader");
	}

	// Every live workspace config file predates this change and is sitting in DELETE mode (all 10 of
	// them, per the note in M001_ConfigBaseline). Simulate that by running the OLD bootstrap path —
	// MigrationRunner.Run without the pragma, exactly what Ensure used to do — then call the FIXED
	// Ensure, which is the sequence prod's config/{ws}.db goes through on its next open after deploy.
	//
	// The rows matter here: config bindings hold ENCRYPTED SECRETS that exist nowhere else, so the
	// conversion has to be proven non-destructive, not merely successful.
	[Fact]
	public void Ensure_MigratesExistingDeleteModeConfigDbToWal_WithoutTouchingRows()
	{
		var cs = Cs("existing");

		MigrationRunner.Run(cs, typeof(M001_ConfigBaseline).Assembly, SqliteTier.Durable);
		ReadJournalMode(cs).Should().Be("delete",
			"sanity check: without the pragma a fresh file defaults to journal_mode=DELETE, matching every "
			+ "workspace config file that predates this change");

		using (var conn = new SqliteConnection(cs))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText =
				"INSERT INTO ConfigBindings (Path, Value, Tags, CreatedAt, UpdatedAt) "
				+ "VALUES ('a/plain', 'v', '', '2026-07-28T00:00:00Z', '2026-07-28T00:00:00Z');";
			cmd.ExecuteNonQuery();
		}

		ConfigSchema.Ensure(cs);

		ReadJournalMode(cs).Should().Be("wal",
			"journal_mode is persistent in the file header, so an EXISTING pre-change config db must "
			+ "convert to WAL the first time Ensure runs after deploy — in place, with no migration and no "
			+ "data movement, same as core.db's and the log tier's conversion");

		Convert.ToInt64(Scalar(cs, "SELECT COUNT(*) FROM ConfigBindings;")).Should().Be(1,
			"the conversion must not disturb a single row — config bindings carry encrypted secrets that "
			+ "are reproducible from nowhere else");
		Convert.ToString(Scalar(cs, "SELECT Value FROM ConfigBindings WHERE Path='a/plain';")).Should().Be("v");
		Convert.ToInt64(Scalar(cs, "SELECT COUNT(*) FROM VersionInfo;")).Should().BeGreaterThan(0,
			"Ensure re-runs MigrateUp against the same file; it must adopt it, not recreate it");
	}

	// The sidecars WAL introduces are the reason to check this at all: config files are enumerated
	// and deleted by the shared ScopedDbFiles helpers, and a `*.db` glob that also matched
	// `{ws}.db-wal` would make orphan-cleanup and Backup see phantom databases.
	[Fact]
	public void TheWalSidecars_AreNotMistakenForDatabasesByTheSharedFileHelpers()
	{
		var name = "sidecars";
		var cs = Cs(name);
		ConfigSchema.Ensure(cs);

		// Force a -wal sidecar to actually exist by writing without checkpointing.
		using (var conn = new SqliteConnection(cs))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText =
				"INSERT INTO ConfigBindings (Path, Value, Tags, CreatedAt, UpdatedAt) "
				+ "VALUES ('x', 'y', '', '2026-07-28T00:00:00Z', '2026-07-28T00:00:00Z');";
			cmd.ExecuteNonQuery();
		}

		File.Exists(Path.Combine(_dir, name + ".db-wal")).Should().BeTrue(
			"positive control — if no sidecar was produced, the assertion below would pass vacuously and "
			+ "prove nothing about how the helpers treat one");

		ScopedDbFiles.ListRootScopeKeys(_dir).Should().NotContain(k => k.EndsWith("-wal", StringComparison.Ordinal),
			"ScopedDbFiles.ListRootScopeKeys globs *.db, and orphan-cleanup deletes what it does not "
			+ "recognise — a sidecar counted as a database would be a data-destroying false positive");
		ScopedDbFiles.ListRootScopeKeys(_dir).Should().Contain(name,
			"the real config file must still be found");
	}
}
