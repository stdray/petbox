using FluentMigrator.Runner;
using FluentMigrator.Runner.Initialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data.Migrations;

// FluentMigrator ships its OWN `MigrationRunner` in FluentMigrator.Runner, which this file must
// import for IMigrationRunner/AddSQLite. The alias keeps `MigrationRunner.Run(...)` below meaning
// the PRODUCTION bootstrap — and keeps it spelled that way, which is what
// Architecture/TestSchemaBypassGuardTests scans for and allowlists this file on.
using MigrationRunner = PetBox.Core.Data.MigrationRunner;

namespace PetBox.Tests.Data.Schema;

// M055 strips the retired `agents:read` / `agents:write` tokens out of ApiKeys.Scopes
// (work retire-agents-scopes-from-live-keys). This is a DATA migration, and that changes what a test
// has to be: GoldenSchemaTests cannot see it at all — the schema is byte-identical before and after —
// so if these assertions are wrong, nothing else in the suite disagrees.
//
// EVERY FIXTURE STRING BELOW IS A REAL PRODUCTION VALUE, copied from the 2026-09-06 `apikey_list`
// census of all 17 affected keys, not a shape invented to be convenient. That matters for exactly one
// reason: the retired tokens are not at the end. In $system's `agent` key they sit between
// `agent:heartbeat` and `admin:provision`; in smoke's sandbox key they are the FIRST two tokens. A
// filter that reordered, or one that trimmed a leading/trailing separator by hand, passes a
// tail-only fixture and silently rewrites grants on the real rows.
//
// The three claims that would each be an invisible data bug:
//   * survivors keep their VALUE, ORDER and MULTIPLICITY (Retires_only_the_retired_*, Duplicates_*);
//   * a row with nothing retired is not written AT ALL, so no separator normalization leaks onto rows
//     this card was never about (Leaves_untouched_*, Rewrite_returns_null_*);
//   * a row is never emptied (Leaves_a_key_whose_only_scopes_*) — see M055's own comment for why an
//     empty column is not the same thing as "only unknown scopes" to McpToolScopeFilter.
public sealed class RetireAgentScopesMigrationTests : IDisposable
{
	// The last version BEFORE the cleanup. Migrating to exactly this point reconstructs the state a
	// live core.db is in when the deploy carrying M055 starts: keys minted while `agents:read` was
	// still a catalog scope.
	const long BeforeTheCleanup = 54;
	const long TheCleanup = 55;

	// Real row, $system/`agent`: the retired pair sits in the MIDDLE, with the root-equivalent
	// `admin:provision` behind it.
	const string SystemAgentBefore =
		"config:read,config:write,logs:ingest,logs:query,logs:admin,health:write,data:read,data:write,"
		+ "data:schema,tasks:read,tasks:write,methodology:write,memory:read,memory:write,llm:invoke,"
		+ "llm:admin,deploy:read,deploy:write,agent:poll,agent:heartbeat,agents:read,agents:write,"
		+ "admin:provision";

	const string SystemAgentAfter =
		"config:read,config:write,logs:ingest,logs:query,logs:admin,health:write,data:read,data:write,"
		+ "data:schema,tasks:read,tasks:write,methodology:write,memory:read,memory:write,llm:invoke,"
		+ "llm:admin,deploy:read,deploy:write,agent:poll,agent:heartbeat,admin:provision";

	// Real row, smoke/`verify-11681d6-smoke-sandbox`: the retired pair is at the very FRONT.
	const string SmokeBefore =
		"agents:read,agents:write,config:read,config:write,memory:read,memory:write,tasks:read,"
		+ "tasks:write,data:read,data:write";

	const string SmokeAfter =
		"config:read,config:write,memory:read,memory:write,tasks:read,tasks:write,data:read,data:write";

	// Real row, kek-devices/`agent-wsl`: one retired token, at the end.
	const string KekBefore = "tasks:read,tasks:write,tasks:approve,memory:read,memory:write,agents:read";
	const string KekAfter = "tasks:read,tasks:write,tasks:approve,memory:read,memory:write";

	// Real row, pochtar/`agent` (the older of the two): carries nothing retired at all.
	const string CleanBefore =
		"config:read,config:write,logs:ingest,logs:query,logs:admin,health:write,health:read,data:read,"
		+ "data:write,data:schema,tasks:read,tasks:write,tasks:approve,memory:read,memory:write,"
		+ "llm:invoke,llm:admin,deploy:read,deploy:write,agent:poll,agent:heartbeat";

	readonly string _dir;

	public RetireAgentScopesMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-retire-agentscopes-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	// ── the three production shapes ───────────────────────────────────────────────────────────

	[Fact]
	public void Retires_only_the_retired_tokens_and_keeps_every_survivor_in_order()
	{
		var cs = MigratedToBeforeTheCleanup("mixed");

		// The REAL bootstrap, the same call the server makes on startup.
		MigrationRunner.Run(cs);

		Applied(cs).Should().Contain(TheCleanup);

		// Whole-string equality, deliberately, rather than "does not contain agents:read": a set
		// comparison would pass a migration that reordered the survivors or dropped a neighbour, and
		// `admin:provision` is the neighbour immediately behind the retired pair on this very row.
		Scopes(cs, "yb_key_mid").Should().Be(SystemAgentAfter,
			"the retired pair sits mid-string — everything before AND after it survives, in order");
		Scopes(cs, "yb_key_front").Should().Be(SmokeAfter,
			"the retired pair leads this row — removing it must not leave a dangling separator");
		Scopes(cs, "yb_key_tail").Should().Be(KekAfter,
			"and the trailing case must not eat the token in front of it");
	}

	// Rule 3 of M055: an all-retired row keeps its dead tokens rather than becoming "". An empty
	// column is NOT the neutral value it looks like — McpToolScopeFilter reads an empty granted set
	// as "no claim -> show all tools", the branch meant for cookie identities, so emptying this row
	// would flip its tools/list from "filtered to nothing" to the whole catalog. No access widens
	// (AssertScope still denies every call), but a data migration must not change what a key
	// observes. Zero of the 17 real keys are in this state; the branch is a guard, not a live case.
	[Fact]
	public void Leaves_a_key_whose_only_scopes_are_retired_exactly_as_it_was()
	{
		var cs = MigratedToBeforeTheCleanup("alldead");

		MigrationRunner.Run(cs);

		Scopes(cs, "yb_key_alldead").Should().Be("agents:read,agents:write",
			"emptying the column is a different state from 'only unknown scopes', not a tidier "
			+ "spelling of it — see M055's rule 3");
	}

	// Rule 1: a row with nothing retired is not written at all. The second fixture is the load-bearing
	// one — ApiKeyScopes.Validate accepts space- and semicolon-separated input, so a stored row can
	// legally be non-canonical. Re-joining every row would normalize it, which is a change to data
	// this card is not about, on a row nobody reviewed.
	[Fact]
	public void Leaves_untouched_every_row_that_carries_nothing_retired()
	{
		var cs = MigratedToBeforeTheCleanup("clean");

		MigrationRunner.Run(cs);

		Scopes(cs, "yb_key_clean").Should().Be(CleanBefore);
		Scopes(cs, "yb_key_noncanonical").Should().Be("data:read logs:query;memory:read",
			"a row with nothing to strip must come out byte-for-byte identical — separators included");
	}

	// ── the dirty string ──────────────────────────────────────────────────────────────────────

	// What ApiKeyScopes.Split actually does with whitespace, mixed separators and case, pinned against
	// the rebuild. Padding and separator variety are NORMALIZED here (correct — this row is being
	// rewritten anyway, and comma is what every writer produces), while every survivor's value and
	// position is kept.
	//
	// CASE IS THE DELIBERATE NON-CHANGE. `Agents:Read` is NOT stripped, because ApiKeyScopes.Comparer
	// is Ordinal and the catalog argues at length that a case-insensitive reading anywhere recognizes
	// a permission the catalog does not. Matching it case-insensitively here would make this migration
	// the one place in the codebase that folds case on a scope token. It also cannot occur in real
	// data: Validate checks the same Ordinal set at mint time, so no writer could have stored it.
	[Fact]
	public void Handles_a_dirty_string_without_losing_a_survivor()
	{
		var cs = MigratedToBeforeTheCleanup("dirty");

		MigrationRunner.Run(cs);

		Scopes(cs, "yb_key_dirty").Should().Be("tasks:read,memory:read,Agents:Read,tasks:write",
			"padding and mixed separators collapse to the canonical comma form; 'Agents:Read' survives "
			+ "because scope matching is Ordinal everywhere, this migration included");
	}

	// Multiplicity is preserved, not repaired. Every write path already applies .Distinct(), so a
	// duplicate in the column is an anomaly to leave VISIBLE — de-duplicating it here would be a
	// second, unrequested edit hiding under the first.
	[Fact]
	public void Duplicates_among_the_survivors_are_preserved_not_collapsed()
	{
		var cs = MigratedToBeforeTheCleanup("dupes");

		MigrationRunner.Run(cs);

		Scopes(cs, "yb_key_dupes").Should().Be("tasks:read,tasks:read,memory:read");
	}

	// ── restart and empty-database safety ─────────────────────────────────────────────────────

	// A deploy is not a single run: the service restarts and the bootstrap runs again. VersionInfo
	// gates the re-run, and the rewrite is idempotent besides — a second pass over already-cleaned
	// rows would find nothing to strip.
	[Fact]
	public void Running_the_bootstrap_again_changes_nothing()
	{
		var cs = MigratedToBeforeTheCleanup("twice");
		MigrationRunner.Run(cs);
		var after = AllScopes(cs);

		var act = () => MigrationRunner.Run(cs);

		act.Should().NotThrow();
		AllScopes(cs).Should().Equal(after, "a restart must not move a single character");
	}

	// The migration runs on every database, including ones that never held these tokens — a fresh
	// install, and any deployment whose keys were all re-scoped through the UI first. Finding nothing
	// to do is the expected outcome there, not an error.
	[Fact]
	public void A_clean_database_migrates_with_no_keys_to_fix()
	{
		var cs = ConnectionString("fresh");

		var act = () => MigrationRunner.Run(cs);

		act.Should().NotThrow("no rows to rewrite is not a failure");
		Applied(cs).Should().Contain(TheCleanup);
	}

	// ── the decision function, where "not written" is distinguishable from "written identically" ──

	// The DB tests above can only observe the VALUE of a row; they cannot see whether an UPDATE was
	// issued that happened to write the same bytes. This pins the difference at the source: null means
	// the row is skipped entirely.
	[Theory]
	[InlineData(CleanBefore)]
	[InlineData("data:read logs:query;memory:read")]
	[InlineData("agents:read,agents:write")]  // all-retired: skipped, never emptied
	[InlineData("")]
	public void Rewrite_returns_null_for_every_row_that_must_not_be_written(string stored) =>
		M055_RetireAgentScopesFromKeys.Rewrite(stored).Should().BeNull();

	[Fact]
	public void Rewrite_returns_the_new_value_only_when_something_was_actually_retired() =>
		M055_RetireAgentScopesFromKeys.Rewrite(SystemAgentBefore).Should().Be(SystemAgentAfter);

	// ── plumbing ──────────────────────────────────────────────────────────────────────────────

	string ConnectionString(string name) => $"Data Source={Path.Combine(_dir, name + ".db")}";

	// A core.db as it exists on the server the moment before the deploy carrying M055: the full
	// migration set applied up to (and including) M054, and ApiKeys rows carrying the strings the
	// production census found.
	//
	// The partial MigrateUp is hand-built rather than routed through MigrationRunner: the production
	// entry point deliberately exposes no "migrate to version N" door (there is exactly one target —
	// latest), and inventing one in product code to serve a test would be worse than the six lines
	// below. Everything about WHICH migrations run — the assembly and the namespace filter that keeps
	// the disk-cache set out — is read off the production type, not restated.
	string MigratedToBeforeTheCleanup(string name)
	{
		var cs = ConnectionString(name);
		MigrateTo(cs, BeforeTheCleanup);

		Applied(cs).Should().NotContain(TheCleanup,
			"the fixture must not have jumped past the migration under test");

		Exec(cs, $"""
			INSERT INTO Projects (Key, WorkspaceKey, Name, Description) VALUES ('p', '$system', 'P', '');
			INSERT INTO ApiKeys (Key, ProjectKey, Scopes, CreatedAt, Name) VALUES
			  ('yb_key_mid',          'p', '{SystemAgentBefore}', '2026-09-06 00:00:00', 'mid'),
			  ('yb_key_front',        'p', '{SmokeBefore}',       '2026-09-06 00:00:00', 'front'),
			  ('yb_key_tail',         'p', '{KekBefore}',         '2026-09-06 00:00:00', 'tail'),
			  ('yb_key_clean',        'p', '{CleanBefore}',       '2026-09-06 00:00:00', 'clean'),
			  ('yb_key_noncanonical', 'p', 'data:read logs:query;memory:read', '2026-09-06 00:00:00', 'nc'),
			  ('yb_key_alldead',      'p', 'agents:read,agents:write', '2026-09-06 00:00:00', 'alldead'),
			  ('yb_key_dupes',        'p', 'tasks:read,agents:read,tasks:read,memory:read', '2026-09-06 00:00:00', 'dupes'),
			  ('yb_key_dirty',        'p', ' tasks:read ,  agents:read ;memory:read  Agents:Read;;agents:write, tasks:write ', '2026-09-06 00:00:00', 'dirty');
			""");

		return cs;
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

	static string Scopes(string cs, string key) =>
		(string)Scalar(cs, $"SELECT Scopes FROM ApiKeys WHERE \"Key\" = '{key}'")!;

	static IReadOnlyList<string> AllScopes(string cs)
	{
		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT \"Key\" || '=' || Scopes FROM ApiKeys ORDER BY \"Key\"";
		using var reader = cmd.ExecuteReader();
		var rows = new List<string>();
		while (reader.Read()) rows.Add(reader.GetString(0));
		return rows;
	}

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
