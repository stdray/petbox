using FluentMigrator.Runner;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;
using PetBox.Core.Models;

namespace PetBox.Tests.Memory;

// M032 widens the system-store taxonomy: existing `autocaptured`/`canon` MemoryStores rows
// (created before the widening with IsSystem=0) must be flipped to IsSystem=1 by the backfill.
// Faithful test: migrate up to M031, seed legacy rows, then run M032 — the same wiring the
// prod DB gets. Case-insensitive predicate mirrors how the flag is computed at creation.
public sealed class MemoryStoreSystemBackfillMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public MemoryStoreSystemBackfillMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-membackfill-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M032_BackfillsIsSystem_OnExistingAutocapturedAndCanon_CaseInsensitive_LeavesOthers()
	{
		// 1) Old schema state: everything up to and including M031 (IsSystem column exists,
		//    but the widening has not run yet).
		MigrateTo(31);

		// 2) Legacy rows as they'd exist on prod: created before the widening, IsSystem=0.
		//    A mixed-case name proves the case-insensitive predicate; an ordinary store and a
		//    pre-marked session-digests are controls.
		Exec("""
			INSERT INTO MemoryStores (ProjectKey, Name, Description, CreatedAt, UpdatedAt, IsSystem) VALUES
				('proj', 'autocaptured', NULL, '2026-01-01', '2026-01-01', 0),
				('proj', 'canon',        NULL, '2026-01-01', '2026-01-01', 0),
				('proj', 'CanoN',        NULL, '2026-01-01', '2026-01-01', 0),
				('proj', 'notes',        NULL, '2026-01-01', '2026-01-01', 0),
				('proj', 'session-digests', NULL, '2026-01-01', '2026-01-01', 1);
			""");

		// 3) The widening migration runs.
		MigrateTo(32);

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var byName = db.MemoryStores.Where(s => s.ProjectKey == "proj").ToDictionary(s => s.Name, s => s.IsSystem);

		byName["autocaptured"].Should().BeTrue();
		byName["canon"].Should().BeTrue();
		byName["CanoN"].Should().BeTrue();            // case-insensitive backfill
		byName["session-digests"].Should().BeTrue();  // already system, untouched
		byName["notes"].Should().BeFalse();           // ordinary knowledge store, left alone
	}

	void Exec(string sql)
	{
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	void MigrateTo(long version)
	{
		using var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(_cs)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider();
		using var scope = services.CreateScope();
		scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
	}
}
