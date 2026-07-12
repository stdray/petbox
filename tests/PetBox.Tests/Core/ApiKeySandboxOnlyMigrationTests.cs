using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M042 adds ApiKeys.SandboxOnly. It is ADDITIVE and NOT NULL DEFAULT false: keys minted before it
// must still authenticate and read back intact, with the new field false (= no containment check —
// the old "claim decides everything" behavior for every existing key, whether project-scoped or
// wildcard). Staged migration test: migrate to 41 (Projects.Sandbox already exists), seed keys
// against the pre-42 ApiKeys schema, then run M042.
public sealed class ApiKeySandboxOnlyMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public ApiKeySandboxOnlyMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m042-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M042_LeavesExistingKeysValid_WithASandboxOnlyOfFalse()
	{
		MigrateTo(41); // Projects.Sandbox exists; ApiKeys.SandboxOnly does not yet

		Exec("""
			INSERT INTO ApiKeys (Key, ProjectKey, Scopes, Name, CreatedAt) VALUES
				('yb_key_legacy_scoped', 'kpvotes', 'memory:read', 'legacy scoped', '2026-01-01'),
				('yb_key_legacy_wild', '*', 'memory:read', 'legacy wildcard', '2026-01-01');
			""");

		// Past 42 (the migration under test) to the LATEST schema — see
		// ApiKeyDefaultProjectMigrationTests for why a typed PetBoxDb query needs the FULL current
		// schema, not just the version the migration under test lands on.
		MigrateToLatest();

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var keys = db.ApiKeys.ToDictionary(k => k.Key, k => k);

		keys["yb_key_legacy_scoped"].SandboxOnly.Should().BeFalse(
			"a key minted before the sandbox gate must keep writing wherever its claim already authorized it");
		keys["yb_key_legacy_wild"].SandboxOnly.Should().BeFalse();
		keys["yb_key_legacy_wild"].Name.Should().Be("legacy wildcard"); // the row is otherwise untouched
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

	void MigrateToLatest()
	{
		using var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(_cs)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider();
		using var scope = services.CreateScope();
		scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
	}
}
