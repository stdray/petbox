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
	//
	// core.db was the ONE internal db still running in journal_mode=DELETE: every other tier
	// (Tasks/Memory/Sessions/Deploy) applies the pragmas from its own *Schema.Ensure before its
	// migration set, but core.db is bootstrapped here and here alone, and this overload never
	// applied them. Under DELETE a writer takes an EXCLUSIVE lock on the whole file, so a reader
	// concurrent with a writer gets SQLITE_BUSY rather than the pre-write snapshot WAL would hand
	// it — and core.db is precisely the file whose connection count we are about to multiply
	// (PetBoxDb moving behind a factory: one caller-owned connection per call instead of one
	// shared per request). Apply WAL + busy_timeout BEFORE MigrateUp, so the very first schema
	// build already writes the mode into the file header (journal_mode is persistent — set once,
	// survives every reopen).
	//
	// Safe for backups: Backup.SnapshotAll uses VACUUM INTO, which produces a single consistent
	// file with no -wal/-shm sidecar and is explicitly WAL-safe, and it globs "*.db" so the
	// sidecars are never picked up as sources. Safe for the test template: TestSchema
	// checkpoint(TRUNCATE)s and releases the pooled handle before copying the file.
	public static void Run(string connectionString)
	{
		SqlitePragmas.ApplyWal(connectionString);
		Run(connectionString, typeof(Migrations.M001_Initial).Assembly);
	}

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
