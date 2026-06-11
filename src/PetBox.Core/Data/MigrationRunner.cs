using System.Collections.Concurrent;
using System.Reflection;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;

namespace PetBox.Core.Data;

public static class MigrationRunner
{
	// Concurrent MigrateUp() calls against ONE db file race on FluentMigrator's own
	// VersionInfo bootstrap (CREATE TABLE without IF NOT EXISTS) and on any non-idempotent
	// DDL. That happens in-process when parallel test hosts Ensure() the same file —
	// serialize per connection string. (Prod is a single process; cross-process races
	// don't occur.)
	static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

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
		lock (Locks.GetOrAdd(connectionString, _ => new object()))
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
}
