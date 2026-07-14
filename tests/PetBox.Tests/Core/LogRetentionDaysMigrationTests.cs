using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M045 adds Logs.RetentionDays. It is ADDITIVE and NULLABLE: RetentionService.cs falls back to
// the project/workspace/system cascade exactly when this column is NULL, so a log created before
// the column existed must read it back as NULL — that is what makes the migration a true no-op
// for every pre-existing log. Staged migration test: migrate to 44 (before RetentionDays), seed a
// Logs row against that schema, then run M045.
public sealed class LogRetentionDaysMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public LogRetentionDaysMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m045-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void M045_LeavesExistingLogs_WithANullRetentionOverride()
	{
		MigrateTo(44); // the schema as it was before RetentionDays existed

		Exec("""
			INSERT INTO Logs (ProjectKey, Name, Description, CreatedAt, UpdatedAt) VALUES
				('$system', 'petbox', 'self-log', '2026-01-01', '2026-01-01'),
				('kpvotes', 'default', NULL, '2026-01-01', '2026-01-01');
			""");

		// Past 45 (the migration under test) to the LATEST schema — PetBoxDb's shared
		// FluentMappingBuilder is CURRENT-schema, not version-45-schema, so a typed read needs the
		// full chain applied (mirrors ApiKeyDefaultProjectMigrationTests).
		MigrateToLatest();

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var logs = db.Logs.ToDictionary(l => (l.ProjectKey, l.Name), l => l);

		logs[("$system", "petbox")].RetentionDays.Should().BeNull();
		logs[("$system", "petbox")].Description.Should().Be("self-log"); // row is otherwise untouched
		logs[("kpvotes", "default")].RetentionDays.Should().BeNull();
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
