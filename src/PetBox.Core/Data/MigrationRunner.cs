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
		// NAMESPACE-SCOPED, not assembly-wide. PetBox.Core hosts a SECOND, unrelated migration set —
		// the disk cache's (PetBox.Core.Data.CacheMigrations) — and an unfiltered scan would run both
		// sets against both files: core.db would grow a cache_entries table it has no use for, and
		// (far worse) the cache file would be built with the entire Core schema. Neither would fail a
		// build or a startup. Every Core migration lives in exactly one namespace, so the filter is
		// precise rather than approximate.
		Run(connectionString, typeof(Migrations.M001_Initial).Assembly, typeof(Migrations.M001_Initial).Namespace);
	}

	// Runs the migration set found in `migrationsAssembly` against `connectionString`.
	// Used by the per-tier scoped factories (Tasks/Memory/Sessions): each tier owns
	// its migrations in its own assembly, so ScanIn isolates them to that tier's
	// `.db` files (Core migrations never leak into a tasks/memory/sessions file).
	// Each `.db` file keeps its own VersionInfo table, so version numbers are
	// per-tier-independent.
	public static void Run(string connectionString, Assembly migrationsAssembly) =>
		Run(connectionString, migrationsAssembly, migrationsNamespace: null);

	// `migrationsNamespace` narrows the scan to ONE set inside an assembly that holds more than one
	// (nested namespaces included). Null keeps the assembly-wide behaviour, which is what every
	// per-tier assembly wants — each of those owns exactly one set.
	public static void Run(string connectionString, Assembly migrationsAssembly, string? migrationsNamespace)
	{
		lock (Locks.GetOrAdd(connectionString, _ => new object()))
		{
			var services = new ServiceCollection()
				.AddFluentMigratorCore()
				.ConfigureRunner(rb => rb
					.AddSQLite()
					.WithGlobalConnectionString(connectionString)
					.ScanIn(migrationsAssembly).For.Migrations())
				.Configure<FluentMigrator.Runner.Initialization.TypeFilterOptions>(opt =>
				{
					opt.Namespace = migrationsNamespace;
					opt.NestedNamespaces = migrationsNamespace is not null;
				})
				.BuildServiceProvider();

			using var scope = services.CreateScope();
			var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
			// FluentMigrator opens its OWN connection and keeps it for the whole session, so the
			// linq2db hook in SqliteDurability never sees it. Executing the pragma through the
			// processor first opens that connection and configures it for every migration that
			// follows. Statement() returns null in production, so production runs nothing extra.
			var durability = SqliteDurability.Statement(SqliteDurability.Relaxed);
			if (durability is not null)
				runner.Processor.Execute(durability);
			runner.MigrateUp();
		}
	}
}
