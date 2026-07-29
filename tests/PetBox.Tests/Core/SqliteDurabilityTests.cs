using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Data;
using PetBox.Deploy.Data;
using PetBox.Memory.Data;
using PetBox.Sessions.Data;
using PetBox.Tasks.Data;

namespace PetBox.Tests;

// The guard on `PRAGMA synchronous`. Every tier now CHOOSES a durability (SqliteTier) instead of
// inheriting SQLite's default by accident, and these tests are what make the choice real rather
// than aspirational.
//
// WHY THEY READ THE PRAGMA BACK OFF A WORKING CONNECTION. synchronous is per-CONNECTION state and
// is NOT written into the file header the way journal_mode=WAL is. Code that sets it on the
// bootstrap/schema connection would cover that one connection and silently nothing else: no
// exception, no symptom, no failing test — the tier would just quietly stay on whatever it had.
// That failure already happened once in this repo with max_page_count (work
// flaky-quota-exceeded-507), where a green suite sat on top of a quota that did not exist in
// production for months, because the assertion read through a warm pool instead of a real open.
// So nothing here asserts against the shape of the code. Every case opens a connection through the
// tier's PRODUCTION factory — the same door DI hands the running service — and asks SQLite.
//
// These used to need to run in ONE class, sequentially, because each case had to null a
// process-wide override for the length of its own assertion. That override is gone and
// SqliteDurability.Synchronous is now a pure function of the tier, so there is no shared mutable
// state left here and no ordering constraint between these tests at all.
public sealed class SqliteDurabilityTests : IDisposable
{
	// SQLite's own numbering for PRAGMA synchronous: 0 OFF, 1 NORMAL, 2 FULL, 3 EXTRA.
	const long Off = 0;
	const long Normal = 1;
	const long Full = 2;

	readonly string _dir;

	public SqliteDurabilityTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-durability-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	static long Synchronous(DataConnection db) => db.Execute<long>("PRAGMA synchronous;");

	static long Synchronous(SqliteConnection conn)
	{
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA synchronous;";
		return (long)cmd.ExecuteScalar()!;
	}

	string Path_(string name) => Path.Combine(_dir, name + ".db");

	// (tier key, the SqliteTier it is assigned, the pragma value that implies, why it got it).
	// One row per SQLite tier PetBox opens.
	//
	// The assigned tier is CARRIED rather than inferred from the expected value. It used to be
	// derived with `expected == Full ? "Durable" : "Telemetry"`, which was fine while exactly two
	// tiers existed and started printing a lie the moment two of them shared NORMAL — the cache
	// would have been reported as Telemetry in every failure message it ever produced.
	public static TheoryData<string, SqliteTier, long, string> EveryTier => new()
	{
		{ "core", SqliteTier.Durable, Full, "core.db holds projects, users, api keys and the workspace ledger" },
		{ "config", SqliteTier.Durable, Full, "config is workspace configuration a write acknowledged" },
		{ "tasks", SqliteTier.Durable, Full, "task boards are user data" },
		{ "memory", SqliteTier.Durable, Full, "memory entries are user data" },
		{ "sessions", SqliteTier.Durable, Full, "session rows are agent-authored content, not telemetry" },
		{ "deploy", SqliteTier.Durable, Full, "deploy state must survive the machine it describes" },
		{ "data", SqliteTier.Durable, Full, "a pet's own database — petbox acknowledged the write on its behalf" },
		{ "logs", SqliteTier.Telemetry, Normal, "telemetry: already lossy and never read back as an authority" },
		{ "cache", SqliteTier.Derived, Normal, "derived: every byte is reconstructible, so a lost tail is a cache miss" },
	};

	// Opens a connection to `tier` THROUGH ITS PRODUCTION FACTORY and returns what SQLite reports.
	// Deliberately no shortcut for any tier: a helper that built its own DataOptions here would be
	// testing this file instead of the shipping one.
	async Task<long> ReadThroughProductionFactory(string tier)
	{
		switch (tier)
		{
			case "core":
				{
					var cs = SqliteConnectionStrings.WithSharedCache(SqliteConnectionStrings.ForFile(Path_("core")));
					TestSchema.Core(cs);
					using var db = TestCoreDb.CoreFactory(cs).Open();
					return Synchronous(db);
				}
			case "config":
				{
					var cs = SqliteConnectionStrings.ForFile(Path_("config"));
					TestSchema.Config(cs);
					using var db = new ConfigDb(ConfigDb.CreateOptions(cs));
					return Synchronous(db);
				}
			case "tasks":
				{
					var cs = SqliteConnectionStrings.ForFile(Path_("tasks"));
					TestSchema.Tasks(cs);
					using var db = new TasksDb(TasksDb.CreateOptions(cs));
					return Synchronous(db);
				}
			case "memory":
				{
					var cs = SqliteConnectionStrings.ForFile(Path_("memory"));
					TestSchema.Memory(cs);
					using var db = new MemoryDb(MemoryDb.CreateOptions(cs));
					return Synchronous(db);
				}
			case "sessions":
				{
					var cs = SqliteConnectionStrings.ForFile(Path_("sessions"));
					TestSchema.Sessions(cs);
					using var db = new SessionsDb(SessionsDb.CreateOptions(cs));
					return Synchronous(db);
				}
			case "deploy":
				{
					var cs = SqliteConnectionStrings.ForFile(Path_("deploy"));
					TestSchema.Deploy(cs);
					using var db = new DeployDb(DeployDb.CreateOptions(cs));
					return Synchronous(db);
				}
			case "logs":
				{
					var cs = SqliteConnectionStrings.ForFile(Path_("logs"));
					TestSchema.Log(cs);
					using var db = new LogDb(LogDb.CreateOptions(cs));
					return Synchronous(db);
				}
			case "cache":
				{
					// The disk cache is a single fleet-wide file like deploy.db: CacheSchema builds it up
					// front and ICacheDbFactory hands out the connections. Goes through
					// CacheSchema.ConnectionString rather than ForFile because that spelling also carries
					// the Default Timeout — it is the string production actually pools.
					var cs = CacheSchema.ConnectionString(Path.Combine(_dir, "cache.db"));
					CacheSchema.Ensure(cs);
					using var db = new CacheDbFactory(cs).Open();
					return Synchronous(db);
				}
			case "data":
				{
					// The user-data tier has no linq2db context: its production door is
					// IDataDbFactory.OpenAsync, which is also where the quota is re-applied.
					var factory = new DataDbFactory(Path.Combine(_dir, "data"));
					await factory.CreateAsync("proj", "db1", DataDbFactory.DefaultMaxPageCount);
					await using var conn = await factory.OpenAsync("proj", "db1", DataDbFactory.DefaultMaxPageCount);
					return Synchronous(conn);
				}
			default:
				throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown tier in EveryTier.");
		}
	}

	// THE test this work exists for: the value a tier chose is the value a deployed process's
	// working connections actually carry.
	[Theory]
	[MemberData(nameof(EveryTier))]
	public async Task EveryTier_CarriesItsChosenDurabilityOnAWorkingConnection(
		string tier, SqliteTier assigned, long expected, string why)
	{
		var actual = await ReadThroughProductionFactory(tier);

		actual.Should().Be(expected,
			$"the {tier} tier is assigned SqliteTier.{assigned} " +
			$"({why}), and PRAGMA synchronous is per-connection — a value that does not show up HERE, on a " +
			"connection opened through the production factory, is a value that does not exist at runtime no " +
			"matter what the factory source says");
	}

	// Guard the theory: a bug that made every tier report the same thing (a hook wired to one
	// constant, say) would still satisfy eight equality assertions if they all expected the same
	// value. They do not — this pins that they never silently converge onto one.
	[Fact]
	public void TheTiers_DoNotAllExpectTheSameValue()
	{
		var expected = EveryTier.Select(row => row.Data.Item3).Distinct().ToList();

		expected.Should().HaveCount(2,
			"the whole point of the sweep is that the tiers were decided SEPARATELY — if every row " +
			"expected one value, the theory above would pass against a factory that ignored its tier argument");
	}

	// Every SqliteTier member must be exercised on a real connection by the theory above. Without
	// this, adding a tier to the enum and forgetting to open one of its databases here would leave a
	// whole durability class unproven while the suite stayed green — which is the same shape of gap
	// (a decision that exists only in source) that this entire work exists to close.
	[Fact]
	public void EveryDeclaredTier_IsCoveredByTheWorkingConnectionTheory()
	{
		var covered = EveryTier.Select(row => row.Data.Item2).Distinct().ToList();

		covered.Should().BeEquivalentTo(Enum.GetValues<SqliteTier>(),
			"a SqliteTier member with no row here is a durability nobody ever reads back off a " +
			"working connection");
	}

	// The rows must agree with PRODUCTION about what each tier means. A row that paired
	// SqliteTier.Derived with FULL would otherwise just be a wrong expectation the theory then
	// dutifully fails on, pointing at the factory instead of at itself.
	[Fact]
	public void TheExpectedValues_MatchWhatProductionSaysEachTierMeans()
	{
		foreach (var row in EveryTier)
		{
			var (tier, assigned, expected, _) = row.Data;
			var word = expected switch { Full => "FULL", Normal => "NORMAL", _ => "OFF" };

			SqliteDurability.Synchronous(assigned).Should().Be(word,
				$"the '{tier}' row claims SqliteTier.{assigned} means {word}");
		}
	}

	// The pragma is per-CONNECTION and is NOT written into the file header, so a hook that fired
	// only on the first physical open would leave every pooled reuse on whatever the last user set.
	[Fact]
	public void TheChosenValue_SurvivesAPooledReopenOfTheSameFile()
	{
		var cs = SqliteConnectionStrings.ForFile(Path_("pooled"));
		TestSchema.Tasks(cs);

		using (var first = new TasksDb(TasksDb.CreateOptions(cs)))
			Synchronous(first).Should().Be(Full);

		// The first connection is back in the pool now; this one very likely gets it handed back.
		using var second = new TasksDb(TasksDb.CreateOptions(cs));
		Synchronous(second).Should().Be(Full,
			"the pragma rides every logical open, not just the first physical one");
	}

	// THE DENY-LIST QUESTION, settled empirically. `synchronous` is deliberately NOT in
	// DataSqlService.PragmaDenyList (the reasoning is recorded there): a pet may relax durability
	// on its OWN database. What it must NOT be able to do is have that relaxation ride a POOLED
	// handle into somebody else's request — the same structural hazard that put max_page_count on
	// the deny-list.
	//
	// The middle step is a POSITIVE CONTROL, and it is what makes the last one worth reading. It
	// proves the leak channel is genuinely open — the pool really does hand the contaminated handle
	// back, still carrying OFF — so the final assertion is testing a mechanism that works, not a
	// hazard that never existed. Without it, "the reopen says FULL" would be equally satisfied by a
	// pool that had simply given us a fresh connection. Reuse is deterministic here because the
	// file, and therefore the connection string that keys the pool, is unique to this test.
	[Fact]
	public async Task APetsRelaxedDurability_CannotRideThePoolIntoAnotherRequest()
	{
		var factory = new DataDbFactory(Path.Combine(_dir, "denylist"));
		await factory.CreateAsync("proj", "pet", DataDbFactory.DefaultMaxPageCount);

		// A pet's request: allowed to run this, because it is its own database.
		await using (var pet = await factory.OpenAsync("proj", "pet", DataDbFactory.DefaultMaxPageCount))
		{
			await using var cmd = pet.CreateCommand();
			cmd.CommandText = "PRAGMA synchronous = OFF;";
			await cmd.ExecuteNonQueryAsync();
			Synchronous(pet).Should().Be(Off, "the pet's own connection took the setting");
		}

		// Positive control: reopen the SAME connection string WITHOUT going through the
		// factory, i.e. the way a caller that forgot to re-assert would.
		var cs = factory.GetConnectionString("proj", "pet");
		await using (var leaked = new SqliteConnection(cs))
		{
			await leaked.OpenAsync();
			Synchronous(leaked).Should().Be(Off,
				"POSITIVE CONTROL — the pooled handle comes back still carrying the pet's OFF. If this " +
				"ever reports FULL the leak channel closed for some unrelated reason and the assertion " +
				"below stops proving anything");
		}

		// The production door: re-asserts the tier at the top of every open.
		await using (var next = await factory.OpenAsync("proj", "pet", DataDbFactory.DefaultMaxPageCount))
		{
			Synchronous(next).Should().Be(Full,
				"IDataDbFactory.OpenAsync re-asserts SqliteTier.Durable on every open, so a pet's PRAGMA " +
				"expires with its own request instead of riding the pool into the next one — that, and " +
				"not a deny-list entry, is what makes leaving `synchronous` allowed safe");
		}
	}

	// The statement each tier emits. No configuration, no process state, no host override — this is
	// now a pure function of the tier, which is why it can be asserted flat like this.
	[Fact]
	public void EachTier_EmitsItsOwnStatement()
	{
		SqliteDurability.Statement(SqliteTier.Durable).Should().Be("PRAGMA synchronous = FULL;",
			"the durable tiers ASSERT their value rather than relying on SQLite's default — " +
			"a pooled handle carries whatever the previous user left on it");
		SqliteDurability.Statement(SqliteTier.Telemetry).Should().Be("PRAGMA synchronous = NORMAL;");
		SqliteDurability.Statement(SqliteTier.Derived).Should().Be("PRAGMA synchronous = NORMAL;");
	}
}
