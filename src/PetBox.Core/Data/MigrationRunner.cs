using System.Reflection;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace PetBox.Core.Data;

public static class MigrationRunner
{
	// Runs the Core (main petbox.db) migration set.
	public static void Run(string connectionString) =>
		Run(connectionString, typeof(Migrations.M001_Initial).Assembly);

	// Runs the migration set found in `migrationsAssembly` against `connectionString`.
	// Used by the per-tier scoped factories (Tasks/Memory/Sessions): each tier owns
	// its migrations in its own assembly, so ScanIn isolates them to that tier's
	// `.db` files (Core migrations never leak into a tasks/memory/sessions file).
	// Each `.db` file keeps its own VersionInfo table, so version numbers are
	// per-tier-independent.
	public static void Run(string connectionString, Assembly migrationsAssembly)
	{
		var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(connectionString)
				.ScanIn(migrationsAssembly).For.Migrations())
			.BuildServiceProvider();

		using var scope = services.CreateScope();
		var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
		runner.MigrateUp();
	}
}
