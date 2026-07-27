using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests;

// Bridges test setup — which keeps its own PetBoxDb to seed rows and assert against — to the
// services under test, which now take ICoreDbFactory and open their OWN connection per call
// (core-db-behind-factory). The factory points at the SAME core.db file and carries the SAME
// DataOptions, so it reuses the SHARED MappingSchema (never build a per-connection one — that was
// the ~290 MB prod OOM; see PetBoxDb.SharedMappingSchema).
//
// The test's `_db` and the service's connections are DIFFERENT connections to one file, which is
// exactly the production shape. Every core db in the suite is file-backed, so that resolves to the
// same database; a `Data Source=:memory:` core db would NOT work here (a second connection would
// see an empty database) and none of these tests use one.
public static class TestCoreDb
{
	public static ICoreDbFactory Factory(this PetBoxDb db) =>
		new CoreDbFactory(new DataOptions<PetBoxDb>(db.Options));

	public static ICoreDbFactory CoreFactory(string connectionString) =>
		new CoreDbFactory(connectionString);

	// The MCP tools no longer take a core-db factory — they take the SERVICE that owns core.db for
	// their concern (db-access-layer-cleanup: the database is visible only in the service layer).
	// A unit test that drives a tool directly builds the real service over its own factory: these
	// are the production implementations, not stubs, so the tools are exercised through exactly the
	// door DI hands them at runtime.
	public static IWorkspaceMemoryDirectory WorkspaceMemory(this ICoreDbFactory dbf) =>
		new WorkspaceMemoryDirectory(dbf);

	public static PetBox.Core.Health.IHealthReportService HealthReports(this ICoreDbFactory dbf) =>
		new PetBox.Core.Health.HealthReportService(dbf);

	// The pull-endpoint list — a different table from HealthReports above, and its own door.
	public static PetBox.Core.Health.IHealthEndpointDirectory HealthEndpoints(this ICoreDbFactory dbf) =>
		new PetBox.Core.Health.HealthEndpointDirectory(dbf);

	// ApiKeys' one door. The config-key lookup is EMPTY here (no Auth:ApiKeys section in a unit
	// test) — a config-declared key is a host-level concern and has its own integration coverage.
	public static PetBox.Web.Auth.AgentKeyAdminService AgentKeys(this ICoreDbFactory dbf) =>
		new(dbf,
			new PetBox.Core.Auth.KeyStatService(),
			new PetBox.Core.Auth.ConfigApiKeyLookup(
				Microsoft.Extensions.Options.Options.Create(new PetBox.Core.Auth.ConfigApiKeyOptions())));

	public static PetBox.Web.Auth.IProjectDirectory Projects(this ICoreDbFactory dbf) =>
		new PetBox.Web.Auth.ProjectDirectory(dbf);

	public static PetBox.Log.Core.Data.ISavedQueryStore SavedQueries(this ICoreDbFactory dbf) =>
		new PetBox.Log.Core.Data.SavedQueryStore(dbf);

	// WorkspaceMembers' one door, for tests — the REAL production service, not a stub.
	//
	// The table is banned (RS0030, BannedSymbols.txt), and the tests are the reason the ban exists.
	// A membership seeded with a raw `db.Insert(new WorkspaceMember …)` never walks the path
	// production walks, so the bug the test is meant to catch cannot manifest where the assertion
	// looks — this repo has been bitten by that three times. See WorkspaceDeletePageTests for the
	// worst of them: a workspace-delete gate stayed green for two days because the fixture's raw
	// insert never created the service-managed container the gate keys on.
	public static IWorkspaceMembershipService Memberships(this ICoreDbFactory dbf) =>
		new WorkspaceMembershipService(dbf);

	// Seed a membership for an ALREADY-SEEDED user, through the production service. Tests hold a user
	// id (they just inserted the User row); AddMemberAsync is keyed by username because that is what
	// the admin page posts — so resolve the one to the other and go through the real door.
	// `password: null` is deliberate: the account exists, and AddMemberAsync must never overwrite it.
	public static async Task SeedMemberAsync(
		this ICoreDbFactory dbf, long userId, string workspaceKey, WorkspaceRole role)
	{
		string username;
		using (var db = dbf.Open())
			username = db.Users.FirstOrDefault(u => u.Id == userId)?.Username
				?? throw new InvalidOperationException(
					$"SeedMemberAsync: no User row with id {userId} — seed the user before its membership.");

		var outcome = await dbf.Memberships().AddMemberAsync(workspaceKey, username, null, role);
		if (outcome != AddMemberOutcome.Added)
			throw new InvalidOperationException(
				$"SeedMemberAsync: '{username}' → '{workspaceKey}' as {role} returned {outcome}, not Added.");
	}

	// Same door, for the fixtures that hold a PetBoxDb rather than a factory. Its connection and the
	// service's are two connections to ONE file — the production shape (see the note on Factory()).
	public static Task SeedMemberAsync(this PetBoxDb db, long userId, string workspaceKey, WorkspaceRole role) =>
		db.Factory().SeedMemberAsync(userId, workspaceKey, role);

	public static IWorkspaceMembershipService Memberships(this PetBoxDb db) => db.Factory().Memberships();

	// Drop every membership — a fixture RESET, not a production path (nothing in production wipes the
	// table). Still goes through the service, one user at a time, because RemoveUserAsync is the
	// cascade the service owns and the quota ledger is what it keeps honest.
	public static async Task ClearMembershipsAsync(this ICoreDbFactory dbf)
	{
		var members = dbf.Memberships();
		foreach (var userId in (await members.ListAllAsync()).Select(m => m.UserId).Distinct())
			await members.RemoveUserAsync(userId);
	}
}

// GROUND TRUTH — the ONE place in the suite allowed to read WorkspaceMembers raw, and it can only
// READ.
//
// Tests SEED through the production service (TestCoreDb.SeedMemberAsync above) but must ASSERT
// against the TABLE. Asserting through the same service you seeded with lets a bug in that service
// cancel itself out — seed wrong, read wrong, green. So the two directions get two different doors
// on purpose: the write goes through the code under test, the read goes around it.
//
// It returns WorkspaceMemberOf (not the banned entity), so no call site has to name WorkspaceMember
// and the ban stays total everywhere else.
public static class MembershipProbe
{
#pragma warning disable RS0030 // The sanctioned ground-truth read — see the note above.
	public static IReadOnlyList<WorkspaceMemberOf> MembershipRows(this ICoreDbFactory dbf)
	{
		using var db = dbf.Open();
		return [.. db.WorkspaceMembers.Select(m => new WorkspaceMemberOf(m.UserId, m.WorkspaceKey, m.Role))];
	}
#pragma warning restore RS0030

	public static IReadOnlyList<WorkspaceMemberOf> MembershipRows(this PetBoxDb db) =>
		db.Factory().MembershipRows();
}

// Building a tier's schema with FluentMigrator — a fresh DI container, an assembly scan and
// MigrateUp — costs real time and runs in EVERY test constructor (most DB tests are plain
// IDisposable, not a shared fixture, so xUnit's per-test instantiation re-triggers it). Across
// the suite that dwarfs the sum of test times. Pay it ONCE PER TIER PER PROCESS: migrate into a
// template file lazily, then copy that file per test. Isolation is unchanged — each test still
// gets its own physical DB — but setup is a file copy instead of a migration run.
//
// This generalizes what used to be Core-only (BuildCoreTemplate/Core) to every internal tier
// (Tasks/Memory/Sessions/Log/Config/Deploy): `Templated` below is the ONE place that
// builds-once-and-copies; each tier below is just `Templated(tag, itsRealEnsure)`, not a
// copy-pasted builder. Wire the returned delegate into ScopedDbFactory's `ensure` parameter (or
// call it directly) exactly where the tier's real `*Schema.Ensure` used to go — same signature,
// same idempotency contract.
public static class TestSchema
{
	// Builds `tag`'s schema into a fresh temp .db file by calling `ensureSchema` (the tier's REAL
	// production Ensure/MigrationRunner.Run — never re-derived here), checkpoints + releases the
	// handle so the file is a complete, unlocked snapshot, then returns an idempotent
	// Action<string> that clones the template with File.Copy instead of re-running migrations.
	// The build itself is deferred (Lazy, ExecutionAndPublication) so it happens at most once per
	// process, on whichever test first needs this tier, and is safe under parallel test execution.
	static Action<string> Templated(string tag, Action<string> ensureSchema)
	{
		var template = new Lazy<string>(() => BuildTemplate(tag, ensureSchema), LazyThreadSafetyMode.ExecutionAndPublication);
		return connectionString =>
		{
			// Idempotent like the Ensure it replaces: if the file already exists (a
			// WebApplicationFactory test that keeps a static DB open and re-invokes setup per
			// test) it's left untouched — overwriting it would yank the file out from under the
			// live host. Fresh per-test dirs always get a copy.
			var target = new SqliteConnectionStringBuilder(connectionString).DataSource;
			if (File.Exists(target)) return;
			File.Copy(template.Value, target);
		};
	}

	static string BuildTemplate(string tag, Action<string> ensureSchema)
	{
		var path = Path.Combine(Path.GetTempPath(), $"petbox-tmpl-{tag}-" + Guid.NewGuid().ToString("N") + ".db");
		ensureSchema($"Data Source={path}");
		// Fold any WAL back into the .db file and release the OS handle, so the copied
		// snapshot is complete and not locked. No-op if the file is in rollback-journal mode.
		using (var conn = new SqliteConnection($"Data Source={path}"))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
			cmd.ExecuteNonQuery();
		}
		// Release only this template's pooled handle — a global ClearAllPools here would
		// yank pooled connections out from under tests already running in parallel.
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"));
		return path;
	}

	// One Templated(...) per tier. The field initializers just close over a Lazy<string> — cheap —
	// so nothing is actually built until the first real test in that tier calls the method below.
	static readonly Action<string> CoreEnsure = Templated("core", MigrationRunner.Run);
	static readonly Action<string> TasksEnsure = Templated("tasks", PetBox.Tasks.Data.TasksSchema.Ensure);
	static readonly Action<string> MemoryEnsure = Templated("memory", PetBox.Memory.Data.MemorySchema.Ensure);
	static readonly Action<string> SessionsEnsure = Templated("sessions", PetBox.Sessions.Data.SessionsSchema.Ensure);
	static readonly Action<string> LogEnsure = Templated("log", PetBox.Log.Core.Data.LogSchema.Ensure);
	static readonly Action<string> ConfigEnsure = Templated("config", PetBox.Config.Data.ConfigSchema.Ensure);
	static readonly Action<string> DeployEnsure = Templated("deploy", PetBox.Deploy.Data.DeploySchema.Ensure);

	// Materialize the Core schema at the DB file named by `connectionString` — a drop-in
	// replacement for MigrationRunner.Run(cs) in test setup that copies the migrated template
	// instead of re-running every migration.
	public static void Core(string connectionString) => CoreEnsure(connectionString);

	// Drop-in replacements for TasksSchema.Ensure / MemorySchema.Ensure / SessionsSchema.Ensure /
	// LogSchema.Ensure / ConfigSchema.Ensure / DeploySchema.Ensure in test setup — same signature,
	// so each is also valid wherever the production Ensure was passed as a bare delegate (e.g.
	// ScopedDbFactory's `ensure` constructor parameter).
	public static void Tasks(string connectionString) => TasksEnsure(connectionString);
	public static void Memory(string connectionString) => MemoryEnsure(connectionString);
	public static void Sessions(string connectionString) => SessionsEnsure(connectionString);
	public static void Log(string connectionString) => LogEnsure(connectionString);
	public static void Config(string connectionString) => ConfigEnsure(connectionString);
	public static void Deploy(string connectionString) => DeployEnsure(connectionString);

	// A `Data Source=...;Cache=Shared` connection string for a WebApplicationFactory
	// test's Core db, rooted in a FRESH per-call directory — not a bare filename dropped
	// directly in Path.GetTempPath(). Program.cs derives every scoped module's storage
	// root (logs/config/tasks/memory/db) from Path.GetDirectoryName(this connection
	// string's DataSource); a unique FILENAME with a shared bare-temp-root DIRECTORY still
	// collapses onto ONE physical folder across every test host that uses this idiom, so
	// unrelated test classes' WebApplicationFactory instances all end up racing
	// uncoordinated schema-create + writes against the exact same physical SQLite files —
	// most commonly logs/$system/petbox.db (the self-log, auto-created at startup whenever
	// Features:Logging is on, which is the Testing-environment default) and, for the
	// log-pipeline tests, logs/$system/default.db. That's the mechanism behind the
	// intermittent "no such table" log-pipeline flake and (suspected on Linux CI, where
	// SQLite's POSIX advisory locking is weaker than Windows' mandatory locking) the
	// LogPipelineTests exit-134 SIGABRT: concurrent, uncoordinated DDL/writes to one file
	// from many independent ScopedDbFactory instances. Giving each call its own directory
	// isolates every derived module directory per test host, same as the Core db file itself.
	public static string NewTempConnectionString(string prefix = "petbox-test")
	{
		var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		return $"Data Source={Path.Combine(dir, "petbox.db")};Cache=Shared";
	}
}
