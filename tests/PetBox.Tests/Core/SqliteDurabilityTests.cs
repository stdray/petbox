using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Data;
using PetBox.Deploy.Data;
using PetBox.Log.Core.Data;
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
// Everything lives in ONE class on purpose: xUnit runs a class's tests sequentially, and the
// production-configuration cases have to null the process-wide SqliteDurability.Relaxed for the
// length of their own assertion. Concurrent tests in other classes may open a connection inside
// that window; the only consequence is that those few commits fsync (slower, never wrong).
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

	// Runs `body` with the process configured EXACTLY as a deployed one: nobody has assigned
	// Relaxed, so each tier gets the value its own factory chose. Restores the test host's
	// relaxation afterwards so the rest of the suite keeps its fsync-free run.
	static async Task AsDeployed(Func<Task> body)
	{
		var saved = SqliteDurability.Relaxed;
		SqliteDurability.Relaxed = null;
		try { await body(); }
		finally { SqliteDurability.Relaxed = saved; }
	}

	// (tier, expected pragma value, why that tier gets it). One row per SQLite tier PetBox opens.
	public static TheoryData<string, long, string> EveryTier => new()
	{
		{ "core", Full, "core.db holds projects, users, api keys and the workspace ledger" },
		{ "config", Full, "config is workspace configuration a write acknowledged" },
		{ "tasks", Full, "task boards are user data" },
		{ "memory", Full, "memory entries are user data" },
		{ "sessions", Full, "session rows are agent-authored content, not telemetry" },
		{ "deploy", Full, "deploy state must survive the machine it describes" },
		{ "data", Full, "a pet's own database — petbox acknowledged the write on its behalf" },
		{ "logs", Normal, "telemetry: the only tier whose tail is worth less than the fsync guarding it" },
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
	public async Task EveryTier_CarriesItsChosenDurabilityOnAWorkingConnection(string tier, long expected, string why)
	{
		await AsDeployed(async () =>
		{
			var actual = await ReadThroughProductionFactory(tier);

			actual.Should().Be(expected,
				$"the {tier} tier is assigned {(expected == Full ? "SqliteTier.Durable" : "SqliteTier.Telemetry")} " +
				$"({why}), and PRAGMA synchronous is per-connection — a value that does not show up HERE, on a " +
				"connection opened through the production factory, is a value that does not exist at runtime no " +
				"matter what the factory source says");
		});
	}

	// Guard the theory: a bug that made every tier report the same thing (a hook wired to one
	// constant, say) would still satisfy eight equality assertions if they all expected the same
	// value. They do not — this pins that they never silently converge onto one.
	[Fact]
	public void TheTiers_DoNotAllExpectTheSameValue()
	{
		var expected = EveryTier.Select(row => row.Data.Item2).Distinct().ToList();

		expected.Should().HaveCount(2,
			"the whole point of the sweep is that the tiers were decided SEPARATELY — if every row " +
			"expected one value, the theory above would pass against a factory that ignored its tier argument");
	}

	// The pragma is per-CONNECTION and is NOT written into the file header, so a hook that fired
	// only on the first physical open would leave every pooled reuse on whatever the last user set.
	[Fact]
	public async Task TheChosenValue_SurvivesAPooledReopenOfTheSameFile()
	{
		await AsDeployed(() =>
		{
			var cs = SqliteConnectionStrings.ForFile(Path_("pooled"));
			TestSchema.Tasks(cs);

			using (var first = new TasksDb(TasksDb.CreateOptions(cs)))
				Synchronous(first).Should().Be(Full);

			// The first connection is back in the pool now; this one very likely gets it handed back.
			using var second = new TasksDb(TasksDb.CreateOptions(cs));
			Synchronous(second).Should().Be(Full,
				"the pragma rides every logical open, not just the first physical one");

			return Task.CompletedTask;
		});
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
		await AsDeployed(async () =>
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
		});
	}

	[Fact]
	public void TheTestHost_RelaxesEveryTierItOpens()
	{
		// No AsDeployed here: this is the suite's own configuration, exactly as TestDurability.cs
		// leaves it.
		var tasks = SqliteConnectionStrings.ForFile(Path_("test-tasks"));
		TestSchema.Tasks(tasks);
		using (var db = new TasksDb(TasksDb.CreateOptions(tasks)))
			Synchronous(db).Should().Be(Off, "tasks tier");

		var memory = SqliteConnectionStrings.ForFile(Path_("test-memory"));
		TestSchema.Memory(memory);
		using (var db = new MemoryDb(MemoryDb.CreateOptions(memory)))
			Synchronous(db).Should().Be(Off, "memory tier");

		// The relaxation overrides the TIER, not merely SQLite's default — the log tier would be
		// NORMAL in production and is OFF here like everything else.
		var logs = SqliteConnectionStrings.ForFile(Path_("test-logs"));
		TestSchema.Log(logs);
		using (var db = new LogDb(LogDb.CreateOptions(logs)))
			Synchronous(db).Should().Be(Off, "log tier, whose production value is NORMAL rather than FULL");
	}

	[Fact]
	public void TheChoiceIsAlwaysEmitted_AndTheTestOverrideOutranksEveryTier()
	{
		var saved = SqliteDurability.Relaxed;
		SqliteDurability.Relaxed = null;
		try
		{
			SqliteDurability.Statement(SqliteTier.Durable).Should().Be("PRAGMA synchronous = FULL;",
				"the durable tiers now ASSERT their value rather than relying on SQLite's default — " +
				"a pooled handle carries whatever the previous user left on it");
			SqliteDurability.Statement(SqliteTier.Telemetry).Should().Be("PRAGMA synchronous = NORMAL;");

			SqliteDurability.Relaxed = "OFF";
			SqliteDurability.Statement(SqliteTier.Durable).Should().Be("PRAGMA synchronous = OFF;",
				"a host that sets Relaxed is declaring its whole database disposable, so it outranks " +
				"every tier — which is why nothing under src/ may assign it (SqliteDurabilityGuardTests)");
			SqliteDurability.Statement(SqliteTier.Telemetry).Should().Be("PRAGMA synchronous = OFF;");
		}
		finally
		{
			SqliteDurability.Relaxed = saved;
		}
	}
}
