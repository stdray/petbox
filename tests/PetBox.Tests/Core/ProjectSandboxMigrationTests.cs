using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M041 adds Projects.Sandbox. It is ADDITIVE and NOT NULL DEFAULT false: projects created before
// it must still read back intact, with the new column false (= a normal, non-sandbox project —
// the sandbox write gate refuses a sandbox-only key against it, exactly the old shape). Staged
// migration test: migrate to 40, seed a project against the pre-41 schema, then run M041.
public sealed class ProjectSandboxMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public ProjectSandboxMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m041-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M041_LeavesExistingProjectsValid_WithASandboxOfFalse()
	{
		MigrateTo(40); // the schema as it was before Sandbox existed

		Exec("""
			INSERT INTO Projects (Key, WorkspaceKey, Name, Description) VALUES
				('kpvotes', 'ws', 'K', '');
			""");

		// Past 41 (the migration under test) to the LATEST schema: PetBoxDb's shared
		// FluentMappingBuilder mapping for Projects is CURRENT-schema, not version-41-schema — a
		// typed LinqToDB query 404s on "no such column" if the physical schema stops short of a
		// later fluently-declared column (see ApiKeyDefaultProjectMigrationTests for the same trap).
		MigrateToLatest();

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var project = db.Projects.Single(p => p.Key == "kpvotes");

		project.Sandbox.Should().BeFalse("a project that existed before the sandbox flag must not become a sandbox");
		project.Name.Should().Be("K"); // the row is otherwise untouched
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
