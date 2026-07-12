using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;
using PetBox.Core.Models;

namespace PetBox.Tests.Migrations;

// M034 backfills a name onto the seeded $system self-log key (yb_key_system_internal): M004
// seeded it before the Name column existed (M014), so on existing DBs it shows blank. Staged
// migration test: migrate to v33 (Name column present, key seeded empty), also seed an
// already-named key, then run M034 — the seeded key gets 'system-internal' and any
// operator-named key is left untouched. Runs the real FluentMigrator runner (the cached
// TestSchema template is already fully migrated, so it can't observe the pre-34 state).
public sealed class SeededKeyNameMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public SeededKeyNameMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m034-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M034_NamesSeededKey_LeavesAlreadyNamedKeyUntouched()
	{
		// 1) State just before the backfill: Name column exists (M014), the seeded key was
		//    inserted by M004 with an empty Name. Migrate to 33 to reach that point.
		MigrateTo(33);

		// The seeded key already exists (M004). Add an operator-named control key that must
		// NOT be rewritten by the backfill.
		Exec("""
			INSERT INTO ApiKeys (Key, ProjectKey, Scopes, Name, CreatedAt) VALUES
				('yb_key_named_ctl', '$system', 'config:read', 'ci pipeline', '2026-01-01');
			""");

		// 2) The backfill migration runs.
		MigrateTo(34);

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		// Project ONLY the columns under test: the DB is parked at v34, so a full-entity read
		// would select columns added by later migrations (M040's DefaultProjectKey) that this
		// staged schema does not have yet.
		var byKey = db.ApiKeys.Select(k => new { k.Key, k.Name }).ToDictionary(k => k.Key, k => k.Name);

		byKey["yb_key_system_internal"].Should().Be("system-internal"); // seeded key now labelled
		byKey["yb_key_named_ctl"].Should().Be("ci pipeline");           // operator label preserved
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
