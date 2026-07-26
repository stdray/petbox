using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// M048 moves the sandbox project `smoke` out of the `$system` workspace (work
// `smoke-own-workspace-containment`). What the migration is FOR is not "a row has a different
// string" but "the container DERIVABLE from the sandbox is empty" — so that is what these tests
// assert, through WorkspaceMemory.ContainerKeyFor, the same function every production call site
// derives through. Asserting only the column would pass just as happily if the derivation ignored it.
//
// Staged, like ProjectSandboxMigrationTests: migrate to 47, seed the PRODUCTION shape against the
// pre-48 schema, then run M048.
public sealed class SmokeOwnWorkspaceMigrationTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public SmokeOwnWorkspaceMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-m048-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
	}

	public void Dispose()
	{
		SqliteConnection.ClearPool(new SqliteConnection(_cs));
		TestDirs.CleanupOrDefer(_dir);
	}

	// THE POINT OF THE WHOLE CHANGE, stated as the derivation rather than as the column: before, the
	// container reachable from `smoke` was `$workspace` — the owner's cross-project notes and canon.
	[Fact]
	public void M048_MovesSmoke_SoItsDerivedContainerIsNoLongerTheSystemOne()
	{
		MigrateTo(47);
		SeedProductionShape();

		// Before: the sandbox derives the container that holds the owner's memory. This is the
		// pre-condition the migration exists to destroy — asserted, not assumed.
		WorkspaceMemory.ContainerKeyFor(WorkspaceKeyOfSmoke())
			.Should().Be(WorkspaceMemory.SystemContainer,
				"the pre-48 topology is what made a containment bug a real disclosure");

		MigrateToLatest();

		WorkspaceKeyOfSmoke().Should().Be("smoke");
		WorkspaceMemory.ContainerKeyFor(WorkspaceKeyOfSmoke())
			.Should().Be("$ws-smoke")
			.And.NotBe(WorkspaceMemory.SystemContainer,
				"a future derivation bug must land on the sandbox's own container, not the owner's");
	}

	// The container row must NOT be created. Container rows are lazy (WorkspaceMemory
	// .EnsureContainerAsync), and creating `$ws-smoke` eagerly would manufacture exactly the
	// container this change exists to leave empty.
	[Fact]
	public void M048_DoesNotCreateTheSmokeContainerRow()
	{
		MigrateTo(47);
		SeedProductionShape();
		MigrateToLatest();

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		db.Projects.Any(p => p.Key == "$ws-smoke")
			.Should().BeFalse("container rows are lazy — an eagerly created one is a container that can fill up");
	}

	// The sandbox flag is what makes `smoke` a legal smoke target at all (AGENTS.md rule 7 — the flag,
	// not a workspace). A move that dropped it would silently make every live smoke illegal.
	[Fact]
	public void M048_LeavesTheSandboxFlagAndTheRestOfTheRowIntact()
	{
		MigrateTo(47);
		SeedProductionShape();
		MigrateToLatest();

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		var smoke = db.Projects.Single(p => p.Key == "smoke");

		smoke.Sandbox.Should().BeTrue("the sandbox flag is what makes it a legal smoke target");
		smoke.Name.Should().Be("Smoke");
	}

	// Blast radius: `$system`'s own projects must not follow `smoke` out. The UPDATE is keyed on the
	// project key, but a mistyped predicate would move the built-ins too — including `$workspace`,
	// which would relocate the owner's memory rather than isolating the sandbox.
	[Fact]
	public void M048_LeavesTheOtherSystemProjectsWhereTheyWere()
	{
		MigrateTo(47);
		SeedProductionShape();
		MigrateToLatest();

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));

		db.Projects.Single(p => p.Key == "$workspace").WorkspaceKey.Should().Be("$system",
			"the shared-memory container itself must stay in $system — moving it would move the owner's memory");
		db.Projects.Single(p => p.Key == "$system").WorkspaceKey.Should().Be("$system");
	}

	// A fresh installation has no `smoke` project. The migration must be a clean no-op there rather
	// than failing or inventing rows — EVERY new database and every test fixture runs it, and only the
	// one production database has a row to relocate.
	//
	// The workspace assertion is the one that matters, and it is here because the first draft got it
	// wrong: an unconditional INSERT gave every database an empty `smoke` workspace, and two unrelated
	// suites that enumerate seeded workspaces went red. A data migration for one installation's row is
	// inert everywhere that row is absent, or it is a seed wearing a migration's clothes.
	[Fact]
	public void M048_IsANoOpWhenThereIsNoSmokeProject()
	{
		MigrateToLatest(); // straight through, nothing seeded

		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		db.Projects.Any(p => p.Key == "smoke").Should().BeFalse("the migration must not conjure a project");
		db.Workspaces.Any(w => w.Key == "smoke").Should().BeFalse(
			"a database with no smoke project must not acquire a smoke workspace — every fresh install runs this");
	}

	// THE ROLLBACK PATH, EXERCISED. The recovery plan for this change is `MigrateDown(47)`, so the
	// claim that it restores the previous topology is a test, not a promise in a report.
	[Fact]
	public void M048_Down_PutsSmokeBackIn_System()
	{
		MigrateTo(47);
		SeedProductionShape();
		MigrateToLatest();
		WorkspaceKeyOfSmoke().Should().Be("smoke");

		MigrateDownTo(47);

		WorkspaceKeyOfSmoke().Should().Be("$system", "Down is the rollback, and it must actually reverse the move");
		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		db.Projects.Single(p => p.Key == "smoke").Sandbox.Should().BeTrue("a rollback must not cost the sandbox flag");
	}

	// The production row as it stands today: the sandbox project inside the owner's own workspace.
	void SeedProductionShape() => Exec(
		"""
		INSERT INTO Projects (Key, WorkspaceKey, Name, Description, Sandbox) VALUES
			('smoke', '$system', 'Smoke', 'Live-smoke target (AGENTS.md rule 7).', 1);
		""");

	string WorkspaceKeyOfSmoke()
	{
		using var db = new PetBoxDb(PetBoxDb.CreateOptions(_cs));
		return db.Projects.Single(p => p.Key == "smoke").WorkspaceKey;
	}

	void Exec(string sql)
	{
		using var conn = new SqliteConnection(_cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	void WithRunner(Action<IMigrationRunner> run)
	{
		using var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb
				.AddSQLite()
				.WithGlobalConnectionString(_cs)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider();
		using var scope = services.CreateScope();
		run(scope.ServiceProvider.GetRequiredService<IMigrationRunner>());
	}

	void MigrateTo(long version) => WithRunner(r => r.MigrateUp(version));

	void MigrateToLatest() => WithRunner(r => r.MigrateUp());

	void MigrateDownTo(long version) => WithRunner(r => r.MigrateDown(version));
}
