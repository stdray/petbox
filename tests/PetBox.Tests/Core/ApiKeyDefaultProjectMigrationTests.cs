using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M040 adds ApiKeys.DefaultProjectKey. It is ADDITIVE and NULLABLE: keys minted before it must
// still authenticate and read back intact, with the new field NULL (= no default = the old
// "projectKey is required" behavior for a "*" key). Staged migration test: migrate to 39, seed
// keys against the pre-40 schema, then run M040.
public sealed class ApiKeyDefaultProjectMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public ApiKeyDefaultProjectMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m040-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M040_LeavesExistingKeysValid_WithANullDefault()
	{
		MigrateTo(39); // the schema as it was before DefaultProjectKey existed

		Exec("""
			INSERT INTO ApiKeys (Key, ProjectKey, Scopes, Name, CreatedAt) VALUES
				('yb_key_legacy_scoped', 'kpvotes', 'memory:read', 'legacy scoped', '2026-01-01'),
				('yb_key_legacy_wild', '*', 'memory:read', 'legacy wildcard', '2026-01-01');
			""");

		MigrateTo(40);

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var keys = db.ApiKeys.ToDictionary(k => k.Key, k => k);

		keys["yb_key_legacy_scoped"].ProjectKey.Should().Be("kpvotes");
		keys["yb_key_legacy_scoped"].DefaultProjectKey.Should().BeNull();
		keys["yb_key_legacy_wild"].ProjectKey.Should().Be("*");
		keys["yb_key_legacy_wild"].DefaultProjectKey.Should().BeNull();
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
}
