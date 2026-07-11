using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Search;
using PetBox.Sessions.Data;

namespace PetBox.Tests.Sessions;

// Sessions M007 — the migration that took ownership of search_cursor / search_deadletter after
// SqliteIndexCursorStore.EnsureSchema was deleted. Two properties, one of them load-bearing for
// the rollout:
//   * fresh file  — the migration alone produces both tables, with the composite PK the
//                   dead-letter store relies on;
//   * ADOPTION    — a live sessions/{project}.db already has these tables (SessionFactsJob created
//                   them at runtime, behind VersionInfo's back). M007 must run against such a file
//                   WITHOUT "table already exists" and WITHOUT touching the rows already parked in
//                   them. That is what the Schema.Table(...).Exists() guard buys, and it is the
//                   one thing that could break every existing deployment if it regressed.
public sealed class SessionsSearchCursorMigrationTests : IDisposable
{
	// The exact DDL the deleted SqliteIndexCursorStore.EnsureSchema used to execute at runtime:
	// this is what a pre-M007 production file actually contains. Kept here (and ONLY here, in a
	// test that simulates legacy state) to prove M007 adopts it.
	const string LegacyRuntimeDdl = """
		CREATE TABLE IF NOT EXISTS search_cursor (
			IndexName TEXT PRIMARY KEY, Version INTEGER NOT NULL
		);
		CREATE TABLE IF NOT EXISTS search_deadletter (
			IndexName TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL,
			Attempts INTEGER NOT NULL, Dead INTEGER NOT NULL,
			PRIMARY KEY (IndexName, Type, Id)
		);
		""";

	readonly string _dir;
	readonly string _cs;

	public SessionsSearchCursorMigrationTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-sessmig-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "proj.db")}";
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	DataConnection Connect() => new(new DataOptions().UseSQLite(_cs));

	[Fact]
	public void FreshFile_MigrationCreatesBothTables()
	{
		SessionsSchema.Ensure(_cs);

		using var db = Connect();
		Assert.True(TableExists(db, "search_cursor"));
		Assert.True(TableExists(db, "search_deadletter"));
	}

	// An adopted production file keeps the columns the RAW runtime DDL gave it, while a fresh file
	// gets the ones FluentMigrator's typed API emits — so the two shapes must agree, or the tier
	// would carry two different schemas depending on the file's age. They do: the typed API emits
	// the same TEXT/INTEGER columns (only the PK gets a name).
	[Fact]
	public void FreshFile_ColumnTypesMatchTheLegacyRuntimeDdl()
	{
		SessionsSchema.Ensure(_cs);

		using var db = Connect();
		Assert.Equal(
			[("IndexName", "TEXT"), ("Version", "INTEGER")],
			ColumnTypes(db, "search_cursor"));
		Assert.Equal(
			[("IndexName", "TEXT"), ("Type", "TEXT"), ("Id", "TEXT"), ("Attempts", "INTEGER"), ("Dead", "INTEGER")],
			ColumnTypes(db, "search_deadletter"));
	}

	// The dead-letter store addresses an entity by (IndexName, Type, Id) — a composite PK, not a
	// bag of columns. Assert the DECLARED key, so a typed-API slip that dropped a PrimaryKey() call
	// (silently turning InsertOrReplace into an append) fails here.
	[Fact]
	public void FreshFile_DeadletterHasCompositePrimaryKey()
	{
		SessionsSchema.Ensure(_cs);

		using var db = Connect();
		Assert.Equal(["IndexName", "Type", "Id"], PrimaryKeyColumns(db, "search_deadletter"));
		Assert.Equal(["IndexName"], PrimaryKeyColumns(db, "search_cursor"));
	}

	[Fact]
	public async Task FreshFile_CursorStoreRoundTripsThroughMigratedSchema()
	{
		SessionsSchema.Ensure(_cs);
		var store = new SqliteIndexCursorStore(Connect);

		await store.SetCursorAsync("session-facts:s1", 42);
		await store.BumpAttemptsAsync("session-facts:s1", "session", "s1");
		await store.MarkDeadAsync("session-facts:s1", "session", "s1");

		Assert.Equal(42, await store.GetCursorAsync("session-facts:s1"));
		Assert.True(await store.IsDeadAsync("session-facts:s1", "session", "s1"));
		// The composite PK collapses the two writes above onto ONE row, not two.
		using var db = Connect();
		Assert.Equal(1, db.Execute<int>("SELECT COUNT(*) FROM search_deadletter"));
	}

	// *** The rollout test. *** A production sessions file as it exists TODAY: migrated through
	// M006, with search_cursor/search_deadletter conjured at runtime by the job and rows already
	// in them, while VersionInfo has never heard of M007. Applying M007 to it must be a no-op that
	// merely claims the tables.
	[Fact]
	public async Task LegacyRuntimeCreatedTables_AreAdopted_WithoutErrorOrDataLoss()
	{
		SimulatePreM007Production(out var cursorBefore);

		// The upgrade: the new binary opens the file and runs the migration set, M007 included.
		var ex = Record.Exception(() => SessionsSchema.Ensure(_cs));
		Assert.Null(ex);

		// VersionInfo now owns the tables...
		Assert.Contains(7L, AppliedVersions());
		// ...and the parked cursor/dead-letter rows survived the adoption untouched.
		var store = new SqliteIndexCursorStore(Connect);
		Assert.Equal(cursorBefore, await store.GetCursorAsync("session-facts:legacy"));
		Assert.True(await store.IsDeadAsync("session-facts:legacy", "session", "legacy"));
		using var db = Connect();
		Assert.Equal(1, db.Execute<int>("SELECT COUNT(*) FROM search_cursor"));
		Assert.Equal(7, db.Execute<int>("SELECT Attempts FROM search_deadletter"));
	}

	// Re-running the whole set (every startup does) must stay a no-op — including over an adopted
	// file, where M007's guard is the only thing standing between the runner and a CREATE TABLE
	// on an existing table.
	[Fact]
	public void MigrationsAreIdempotent_OnFreshAndAdoptedFiles()
	{
		SessionsSchema.Ensure(_cs);
		Assert.Null(Record.Exception(() => SessionsSchema.Ensure(_cs)));

		SimulatePreM007Production(out _);
		Assert.Null(Record.Exception(() => SessionsSchema.Ensure(_cs)));
		Assert.Null(Record.Exception(() => SessionsSchema.Ensure(_cs)));
	}

	// Rewinds this file to the state a live pre-M007 deployment is in: the two tables exist with
	// the RAW runtime DDL (not the migrator's) and carry the job's parked state, but VersionInfo
	// does not list version 7.
	void SimulatePreM007Production(out long cursorBefore)
	{
		SessionsSchema.Ensure(_cs); // M001..M006 (+M007, which we now undo)

		using var db = Connect();
		db.Execute("DROP TABLE IF EXISTS search_cursor; DROP TABLE IF EXISTS search_deadletter;");
		db.Execute(LegacyRuntimeDdl); // exactly what the deleted EnsureSchema wrote
		db.Execute("INSERT INTO search_cursor (IndexName, Version) VALUES ('session-facts:legacy', 123);");
		db.Execute("""
			INSERT INTO search_deadletter (IndexName, Type, Id, Attempts, Dead)
			VALUES ('session-facts:legacy', 'session', 'legacy', 7, 1);
			""");
		db.Execute("DELETE FROM VersionInfo WHERE Version = 7;");

		Assert.DoesNotContain(7L, AppliedVersions());
		cursorBefore = 123;
	}

	List<long> AppliedVersions()
	{
		using var db = Connect();
		return db.Query<long>("SELECT Version FROM VersionInfo").ToList();
	}

	static List<(string Name, string Type)> ColumnTypes(DataConnection db, string table) =>
		db.Query(r => (r.GetString(1), r.GetString(2)), $"PRAGMA table_info({table})").ToList();

	static bool TableExists(DataConnection db, string table) =>
		db.Execute<int>($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'") == 1;

	// PRAGMA table_info exposes `pk` as the 1-based position of the column in the primary key
	// (0 = not part of it) — so this returns the key's columns in key order.
	static List<string> PrimaryKeyColumns(DataConnection db, string table) =>
		db.Query(r => new { Name = r.GetString(1), Pk = r.GetInt32(5) }, $"PRAGMA table_info({table})")
			.Where(c => c.Pk > 0)
			.OrderBy(c => c.Pk)
			.Select(c => c.Name)
			.ToList();
}
