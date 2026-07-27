using LinqToDB.Data;
using PetBox.Core.Data;
using PetBox.Memory.Data;
using PetBox.Tasks.Data;

namespace PetBox.Tests;

// The guard on test-sqlite-synchronous. The suite runs `PRAGMA synchronous = OFF` because it is
// disk-bound on fsync barriers it gains nothing from; PRODUCTION MUST NOT. These read the pragma
// back out of a connection opened through the production factory in both configurations, so the
// claim "production still fsyncs on every commit" rests on a measurement, not on the shape of the
// code.
//
// Everything here lives in ONE class on purpose: xUnit runs a class's tests sequentially, and the
// production-configuration case has to null the process-wide SqliteDurability.Relaxed for the
// length of its own assertion. Concurrent tests in other classes may open a connection inside that
// window; the only consequence is that those few commits fsync (slower, never wrong).
public sealed class SqliteDurabilityTests : IDisposable
{
	// SQLite's own numbering for PRAGMA synchronous: 0 OFF, 1 NORMAL, 2 FULL, 3 EXTRA.
	const long Off = 0;
	const long Full = 2;

	readonly string _dir;

	public SqliteDurabilityTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-durability-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	static long Synchronous(DataConnection db) => db.Execute<long>("PRAGMA synchronous;");

	string Path_(string name) => Path.Combine(_dir, name + ".db");

	[Fact]
	public void ProductionConfiguration_LeavesCoreDbAtSqlitesOwnFull()
	{
		var saved = SqliteDurability.Relaxed;
		// Exactly what a deployed process has: nobody has ever assigned the property.
		SqliteDurability.Relaxed = null;
		try
		{
			var cs = SqliteConnectionStrings.WithSharedCache(SqliteConnectionStrings.ForFile(Path_("prod-core")));
			TestSchema.Core(cs);
			using var db = TestCoreDb.CoreFactory(cs).Open();

			Synchronous(db).Should().Be(Full,
				"a deployed PetBox must keep SQLite's default durability — an fsync per commit — " +
				"and nothing under src/ assigns SqliteDurability.Relaxed, so this is what it gets");
		}
		finally
		{
			SqliteDurability.Relaxed = saved;
		}
	}

	[Fact]
	public void ProductionConfiguration_LeavesAModuleDbAtSqlitesOwnFull()
	{
		var saved = SqliteDurability.Relaxed;
		SqliteDurability.Relaxed = null;
		try
		{
			var cs = SqliteConnectionStrings.ForFile(Path_("prod-tasks"));
			TasksSchema.Ensure(cs);
			using var db = new TasksDb(TasksDb.CreateOptions(cs));

			Synchronous(db).Should().Be(Full, "the per-module tiers get the same untouched default");
		}
		finally
		{
			SqliteDurability.Relaxed = saved;
		}
	}

	[Fact]
	public void TheTestHost_RelaxesCoreDb()
	{
		var cs = SqliteConnectionStrings.WithSharedCache(SqliteConnectionStrings.ForFile(Path_("test-core")));
		TestSchema.Core(cs);
		using var db = TestCoreDb.CoreFactory(cs).Open();

		Synchronous(db).Should().Be(Off,
			"tests/TestDurability.cs runs as a module initializer, so every connection the " +
			"production factory hands out in a test host skips the commit fsync");
	}

	[Fact]
	public void TheTestHost_RelaxesEveryTierItOpens()
	{
		var tasks = SqliteConnectionStrings.ForFile(Path_("test-tasks"));
		TasksSchema.Ensure(tasks);
		using (var db = new TasksDb(TasksDb.CreateOptions(tasks)))
			Synchronous(db).Should().Be(Off, "tasks tier");

		var memory = SqliteConnectionStrings.ForFile(Path_("test-memory"));
		MemorySchema.Ensure(memory);
		using (var db = new MemoryDb(MemoryDb.CreateOptions(memory)))
			Synchronous(db).Should().Be(Off, "memory tier");
	}

	// The pragma is per-CONNECTION and is NOT written into the file header, so a hook that fired
	// only on the first physical open would leave every pooled reuse on FULL — the same trap that
	// made DataDbFactory's max_page_count quota apply to exactly one throwaway connection.
	[Fact]
	public void TheRelaxation_SurvivesAPooledReopenOfTheSameFile()
	{
		var cs = SqliteConnectionStrings.ForFile(Path_("pooled"));
		TasksSchema.Ensure(cs);

		using (var first = new TasksDb(TasksDb.CreateOptions(cs)))
			Synchronous(first).Should().Be(Off);

		// The first connection is back in the pool now; this one very likely gets it handed back.
		using var second = new TasksDb(TasksDb.CreateOptions(cs));
		Synchronous(second).Should().Be(Off,
			"the pragma rides every logical open, not just the first physical one");
	}

	[Fact]
	public void Statement_IsEmittedOnlyWhenSomethingAskedForIt()
	{
		SqliteDurability.Statement(null).Should().BeNull(
			"the production configuration must not run any PRAGMA at all — that, and not a " +
			"chosen value, is what keeps the deployed default intact");
		SqliteDurability.Statement("OFF").Should().Be("PRAGMA synchronous = OFF;");
	}
}
