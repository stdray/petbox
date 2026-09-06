using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using LinqToDB;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;
using PetBox.Core.Models;

// FluentMigrator ships its OWN `MigrationRunner` in FluentMigrator.Runner, which this file must
// import for IMigrationRunner/AddSQLite. The alias keeps `MigrationRunner.Run(...)` below meaning
// the PRODUCTION bootstrap — and keeps it spelled that way, which is what
// Architecture/TestSchemaBypassGuardTests scans for and allowlists this file on.
using MigrationRunner = PetBox.Core.Data.MigrationRunner;

namespace PetBox.Tests.Data.Schema;

// M054 drops `agent_definitions` (work agent-defs-server-teardown). GoldenSchemaTests already pins
// the CLEAN-database half — a fresh core.db comes out of the full migration set without the table.
// This class pins the half a golden snapshot structurally cannot see, and the one that runs on the
// live server: the UPGRADE of a database that already HAS the table, with rows in it.
//
// That distinction is not pedantry. `agent_definitions` is populated in every live project (that is
// exactly why the drop is unconditional rather than M038's drop-only-if-empty), so the migration
// that matters is the one nobody can rehearse from an empty file — and the failure it would cause is
// not a red test but a deploy that half-applies and a `no such table` on the next project delete.
//
// THE THIRD TEST IS THE ONE THAT NEARLY WAS MISSED. ProjectDeletion.DeleteAsync used to sweep
// `db.AgentDefinitions` as part of the cascade. Dropping the table WITHOUT removing that line leaves
// a delete path that throws `SQLite Error 1: 'no such table: agent_definitions'` — the project
// cannot be deleted at all, and nothing else in the suite would have said so, because the cascade
// only runs against a real migrated database.
public sealed class DropAgentDefinitionsMigrationTests : IDisposable
{
	// The last version BEFORE the drop. Migrating to exactly this point reconstructs the schema a
	// live core.db is on when the deploy carrying M054 starts.
	const long BeforeTheDrop = 53;
	const long TheDrop = 54;

	readonly string _dir;

	public DropAgentDefinitionsMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-drop-agentdefs-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	// ── path 1: a database that already carries the table AND its rows ────────────────────────

	[Fact]
	public void APopulatedDatabase_LosesTheTable_AndKeepsEverythingElse()
	{
		var (cs, _) = MigratedToBeforeTheDrop("populated");

		TableExists(cs, "agent_definitions").Should().BeTrue(
			"the fixture must actually reproduce the pre-drop schema, or the migration below proves nothing");
		Scalar(cs, "SELECT count(*) FROM agent_definitions").Should().Be(2L);

		// The REAL bootstrap, the same call the server makes on startup.
		MigrationRunner.Run(cs);

		TableExists(cs, "agent_definitions").Should().BeFalse("M054 drops it unconditionally");
		Applied(cs).Should().Contain(TheDrop);

		// The rest of the database is untouched — a drop must not be a rebuild. Asked by KEY rather
		// than by total count: the migration set seeds rows of its own ($system, the workspace memory
		// container), so a total is a number about the migrations, not about the data being preserved.
		Scalar(cs, "SELECT count(*) FROM Projects WHERE Key IN ('doomed','keeper')").Should().Be(2L);
		Scalar(cs, "SELECT count(*) FROM ApiKeys WHERE Key = 'yb_key_doomed'").Should().Be(1L);
		Scalar(cs, "SELECT count(*) FROM MemoryStores WHERE ProjectKey = 'doomed'").Should().Be(1L);
	}

	// A deploy is not a single run: the service restarts, the bootstrap runs again, and a migration
	// that is not gated by VersionInfo would fail the second time (the table it drops is gone).
	[Fact]
	public void RunningTheBootstrapAgain_OnADroppedDatabase_IsANoOp()
	{
		var (cs, _) = MigratedToBeforeTheDrop("twice");
		MigrationRunner.Run(cs);

		var act = () => MigrationRunner.Run(cs);

		act.Should().NotThrow("VersionInfo gates the drop — a restart must not try to drop it again");
		TableExists(cs, "agent_definitions").Should().BeFalse();
	}

	// ── path 2: a clean database ──────────────────────────────────────────────────────────────

	// The other half, stated here rather than left implicit in the golden snapshot: a database built
	// from scratch never grows the table at all — M037 creates it and M054 drops it inside the same
	// run, which is what keeps a fresh install and an upgraded server on ONE schema.
	[Fact]
	public void ACleanDatabase_NeverEndsUpWithTheTable()
	{
		var cs = ConnectionString("clean");

		MigrationRunner.Run(cs);

		TableExists(cs, "agent_definitions").Should().BeFalse();
		Applied(cs).Should().Contain(TheDrop).And.Contain(37,
			"M037 stays in the timeline — a migration history is not the current schema");
	}

	// ── the cascade that reached into the dropped table ───────────────────────────────────────

	[Fact]
	public async Task DeletingAProject_StillWorks_AfterTheTableIsGone()
	{
		var (cs, _) = MigratedToBeforeTheDrop("delete");
		MigrationRunner.Run(cs);

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(cs));

		var deleted = await ProjectDeletion.DeleteAsync(db, "doomed");

		deleted.Should().BeTrue(
			"ProjectDeletion swept db.AgentDefinitions until this card removed that line — leaving it in "
			+ "would make every project delete throw 'no such table: agent_definitions' on a migrated database");
		db.Projects.Count(p => p.Key == "doomed").Should().Be(0);
		db.ApiKeys.Count(k => k.ProjectKey == "doomed").Should().Be(0,
			"the rest of the cascade must still run — a delete that stops early is as broken as one that throws");
		db.MemoryStores.Count(m => m.ProjectKey == "doomed").Should().Be(0);
		db.Projects.Count(p => p.Key == "keeper").Should().Be(1,
			"and it must delete ONE project, not the table's worth");
	}

	// ── plumbing ──────────────────────────────────────────────────────────────────────────────

	string ConnectionString(string name) => $"Data Source={Path.Combine(_dir, name + ".db")}";

	// A core.db as it exists on the server the moment before the deploy carrying M054: the full
	// migration set applied up to (and including) M053, real rows in `agent_definitions`, and real
	// rows in the neighbouring tables the delete cascade touches.
	//
	// The partial MigrateUp is hand-built rather than routed through MigrationRunner: the production
	// entry point deliberately exposes no "migrate to version N" door (there is exactly one target —
	// latest), and inventing one in product code to serve a test would be worse than the six lines
	// below. Everything about WHICH migrations run — the assembly and the namespace filter that keeps
	// the disk-cache set out — is read off the production type, not restated.
	(string Cs, string Db) MigratedToBeforeTheDrop(string name)
	{
		var cs = ConnectionString(name);
		MigrateTo(cs, BeforeTheDrop);

		Applied(cs).Should().Contain(37, "the pre-drop fixture must have M037's table");
		Applied(cs).Should().NotContain(TheDrop, "…and must not have jumped past the migration under test");

		Exec(cs, """
			INSERT INTO Projects (Key, WorkspaceKey, Name, Description) VALUES ('doomed', '$system', 'Doomed', '');
			INSERT INTO Projects (Key, WorkspaceKey, Name, Description) VALUES ('keeper', '$system', 'Keeper', '');
			INSERT INTO ApiKeys (Key, ProjectKey, Scopes, CreatedAt, Name)
			  VALUES ('yb_key_doomed', 'doomed', 'tasks:read', '2026-09-06 00:00:00', 'k');
			INSERT INTO MemoryStores (ProjectKey, Name, CreatedAt, UpdatedAt, IsSystem)
			  VALUES ('doomed', 'notes', '2026-09-06 00:00:00', '2026-09-06 00:00:00', 0);
			INSERT INTO agent_definitions (ProjectKey, Key, Version, Json, ActiveFrom, ActiveTo, Created, Updated)
			  VALUES ('doomed', 'default', 1, '{"name":"default","roles":[]}', 1, NULL, '2026-09-06', '2026-09-06');
			INSERT INTO agent_definitions (ProjectKey, Key, Version, Json, ActiveFrom, ActiveTo, Created, Updated)
			  VALUES ('keeper', 'default', 1, '{"name":"default","roles":[]}', 1, NULL, '2026-09-06', '2026-09-06');
			""");

		return (cs, name);
	}

	static void MigrateTo(string connectionString, long version)
	{
		var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(connectionString)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.Configure<TypeFilterOptions>(opt =>
			{
				opt.Namespace = typeof(M001_Initial).Namespace;
				opt.NestedNamespaces = true;
			})
			.BuildServiceProvider();

		using var scope = services.CreateScope();
		scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
	}

	static bool TableExists(string cs, string table) =>
		(long)Scalar(cs, $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}'")! > 0;

	static IReadOnlyList<long> Applied(string cs)
	{
		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT Version FROM VersionInfo ORDER BY Version";
		using var reader = cmd.ExecuteReader();
		var versions = new List<long>();
		while (reader.Read()) versions.Add(reader.GetInt64(0));
		return versions;
	}

	static object? Scalar(string cs, string sql)
	{
		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		return cmd.ExecuteScalar();
	}

	static void Exec(string cs, string sql)
	{
		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}
}
