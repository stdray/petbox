using LinqToDB;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Deploy.Data;
using PetBox.Memory.Data;
using PetBox.Sessions.Data;
using PetBox.Tasks.Data;

namespace PetBox.Tests;

// TestDirs clears SQLite's connection pool per FILE, and the pool is keyed by the connection
// STRING — so teardown has to name every spelling production can open the file with. That list
// was typed out by hand in TestDirs and fell one behind: `;Cache=Shared;Foreign Keys=True`, the
// string PetBoxDb.CreateOptions builds for core.db, was absent, and for twelve test classes the
// pools were never cleared. Nothing failed; the temp dir just stayed locked.
//
// The list is now derived (SqliteConnectionStrings.Spellings). This is the test that keeps the
// derivation honest: it asks each production context what connection string it actually ends up
// with and requires the derivation to contain it. A new decoration, or a new context that adds
// one, fails HERE rather than by quietly leaking handles.
public sealed class SqliteConnectionStringSpellingTests
{
	const string Path_ = @"C:\tmp\petbox-spelling\some.db";

	// What each linq2db context hands to Microsoft.Data.Sqlite, taken from the built options
	// rather than restated. Both inputs a context can receive are covered: the bare file string
	// (module tiers, via ScopedDbFactory) and the shared-cache one (core.db and deploy.db, whose
	// string comes from configuration).
	public static TheoryData<string, string> ProducedConnectionStrings()
	{
		var bare = SqliteConnectionStrings.ForFile(Path_);
		var shared = SqliteConnectionStrings.WithSharedCache(bare);

		return new TheoryData<string, string>
		{
			{ "PetBoxDb/bare", Cs(PetBoxDb.CreateOptions(bare)) },
			{ "PetBoxDb/shared-cache", Cs(PetBoxDb.CreateOptions(shared)) },
			{ "TasksDb/bare", Cs(TasksDb.CreateOptions(bare)) },
			{ "TasksDb/shared-cache", Cs(TasksDb.CreateOptions(shared)) },
			{ "MemoryDb", Cs(MemoryDb.CreateOptions(bare)) },
			{ "SessionsDb", Cs(SessionsDb.CreateOptions(bare)) },
			{ "ConfigDb", Cs(ConfigDb.CreateOptions(bare)) },
			{ "LogDb", Cs(LogDb.CreateOptions(bare)) },
			{ "DeployDb", Cs(DeployDb.CreateOptions(shared)) },
			// The two raw-connection producers in production code.
			{ "ScopedDbFactory/DataDbFactory", bare },
			{ "appsettings core.db", shared },
		};
	}

	static string Cs<T>(DataOptions<T> options) where T : LinqToDB.Data.DataConnection =>
		options.Options.ConnectionOptions.ConnectionString
			?? throw new InvalidOperationException("linq2db options carry no connection string");

	[Theory]
	[MemberData(nameof(ProducedConnectionStrings))]
	public void EveryProducedConnectionString_IsCoveredByTheDerivedSpellings(string producer, string connectionString)
	{
		var spellings = SqliteConnectionStrings.Spellings(Path_).ToList();

		spellings.Should().Contain(connectionString,
			$"TestDirs clears one pool per derived spelling, so a string {producer} can open but " +
			"the derivation does not produce is a pool that survives every teardown");
	}

	[Fact]
	public void TheDerivationIsEverySpellingProductionCanOpenAFileWith()
	{
		// Pinned literally ON PURPOSE: this is the one place a change to the wire format has to be
		// looked at rather than propagated silently. Everywhere else the spellings are derived — and
		// this test going red on a new decoration is the mechanism working, not a nuisance.
		//
		// The fifth entry arrived with the disk cache (work/cache-backend-decision), the only file
		// production opens with a `Default Timeout`. It is NOT crossed with the other two decorations,
		// because production never crosses it: the cache file is deliberately never shared-cache and
		// has no foreign keys. Listing the four unreachable combinations would make this list a
		// superset of what production does instead of a description of it.
		SqliteConnectionStrings.Spellings(Path_).Should().Equal(
			$"Data Source={Path_}",
			$"Data Source={Path_};Foreign Keys=True",
			$"Data Source={Path_};Cache=Shared",
			$"Data Source={Path_};Cache=Shared;Foreign Keys=True",
			$"Data Source={Path_};Default Timeout={SqliteConnectionStrings.DefaultTimeoutSeconds}");
	}

	[Fact]
	public void ADecorationIsNeverAppliedTwice()
	{
		var once = SqliteConnectionStrings.WithForeignKeys(SqliteConnectionStrings.ForFile(Path_));

		SqliteConnectionStrings.WithForeignKeys(once).Should().Be(once,
			"a context whose caller already supplied the keyword must not produce a second, " +
			"unpooled-by-teardown spelling");
		SqliteConnectionStrings.WithSharedCache(SqliteConnectionStrings.WithSharedCache(once))
			.Should().Be(SqliteConnectionStrings.WithSharedCache(once));
	}
}
