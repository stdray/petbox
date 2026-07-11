using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Data.Migrations;
using PetBox.Memory.Services;

namespace PetBox.Tests.Memory;

// The one-time layout migration (M009 schema + M010 data): N legacy per-store files
// memory/{project}/{store}.db fold into ONE memory/{project}.db with the store as a column.
// Covers the copy, the row-count verification, idempotence (a re-run duplicates nothing) and
// resumability (an interrupted merge finishes on the next run). The legacy files are deliberately
// LEFT ON DISK — their deletion is a separate, later release.
public sealed class MemoryStoreMergeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly string _memoryDir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;

	public MemoryStoreMergeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memmerge-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_memoryDir = Path.Combine(_dir, "memory");
		_factory = new ScopedDbFactory<MemoryDb>(_memoryDir, Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		TestDirs.CleanupOrDefer(_dir);
	}

	string ProjectDbPath => Path.Combine(_memoryDir, $"{Proj}.db");

	// A legacy per-store file exactly as the pre-merge migration set (M001…M008) left it:
	// entries keyed by (Key, Version) alone, search docs addressed with the constant Type "memory",
	// and the vector cursor named "vector" (it needed no store — the file WAS the store).
	void SeedLegacyStore(string store, params (string Key, string Body)[] entries)
	{
		Directory.CreateDirectory(Path.Combine(_memoryDir, Proj));
		var path = Path.Combine(_memoryDir, Proj, $"{store}.db");
		using var db = new SqliteConnection($"Data Source={path}");
		db.Open();
		Exec(db, """
			CREATE TABLE memory_entries (
				Key TEXT NOT NULL, Version INTEGER NOT NULL, Description TEXT NOT NULL, Body TEXT NOT NULL,
				Tags TEXT NOT NULL, PrevKey TEXT, ActiveFrom INTEGER NOT NULL, ActiveTo INTEGER,
				Created TEXT NOT NULL, Updated TEXT NOT NULL, Type INTEGER NOT NULL DEFAULT 2,
				Metadata TEXT NOT NULL DEFAULT '', PRIMARY KEY (Key, Version));
			CREATE TABLE entry_usage (
				Key TEXT NOT NULL PRIMARY KEY, SurfacedCount INTEGER NOT NULL DEFAULT 0,
				OpenedCount INTEGER NOT NULL DEFAULT 0, LastHitAt TEXT NULL,
				DeliberateCount INTEGER NOT NULL DEFAULT 0);
			CREATE VIRTUAL TABLE search_fts USING fts5(
				Scope UNINDEXED, Type UNINDEXED, Id UNINDEXED, Text, Tags, tokenize='unicode61');
			CREATE TABLE search_vec (
				Scope TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL, Model TEXT NOT NULL,
				Dim INTEGER NOT NULL, Vec BLOB NOT NULL, PRIMARY KEY (Scope, Type, Id));
			CREATE TABLE search_cursor (IndexName TEXT PRIMARY KEY, Version INTEGER NOT NULL);
			CREATE TABLE search_deadletter (
				IndexName TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL, Attempts INTEGER NOT NULL,
				Dead INTEGER NOT NULL, PRIMARY KEY (IndexName, Type, Id));
			""");

		var v = 0;
		foreach (var (key, body) in entries)
		{
			v++;
			// Per-store version space — the very reason (Key, Version) collides across stores.
			Exec(db, $"""
				INSERT INTO memory_entries (Key,Version,Description,Body,Tags,ActiveFrom,ActiveTo,Created,Updated,Type,Metadata)
				VALUES ('{key}', {v}, 'desc {key}', '{body}', 'tag', {v}, NULL, '2026-01-01', '2026-01-01', 2, '');
				""");
			Exec(db, $"INSERT INTO entry_usage (Key, SurfacedCount, OpenedCount, DeliberateCount) VALUES ('{key}', 3, 1, 2);");
			Exec(db, $"INSERT INTO search_fts (Scope, Type, Id, Text, Tags) VALUES ('{Proj}', 'memory', '{key}', 'desc {key} {body}', 'tag');");
			Exec(db, $"INSERT INTO search_vec (Scope, Type, Id, Model, Dim, Vec) VALUES ('{Proj}', 'memory', '{key}', 'm', 2, x'00000000');");
		}
		Exec(db, $"INSERT INTO search_cursor (IndexName, Version) VALUES ('{MemoryCursors.LegacyVector}', {v});");
		Exec(db, "INSERT INTO search_cursor (IndexName, Version) VALUES ('behavior-mining', 42);");
		Exec(db, $"INSERT INTO search_deadletter (IndexName, Type, Id, Attempts, Dead) VALUES ('{MemoryCursors.LegacyVector}', 'memory', 'poison', 5, 1);");
	}

	static void Exec(SqliteConnection db, string sql)
	{
		using var cmd = db.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	static long Scalar(MemoryDb db, string sql) => db.Execute<long>(sql);

	[Fact]
	public void Merge_FoldsEveryLegacyStoreFile_IntoTheProjectFile()
	{
		SeedLegacyStore("notes", ("n1", "alpha body"), ("n2", "beta body"));
		SeedLegacyStore("ops", ("o1", "gamma body"));

		using var db = _factory.NewEnsuredConnection(Proj); // first open runs M001…M010

		// Entries carry the store they came from; keys stay per-store.
		db.Entries.Where(e => e.ActiveTo == null).Select(e => e.Store + "/" + e.Key).ToList()
			.Should().BeEquivalentTo(["notes/n1", "notes/n2", "ops/o1"]);
		// Usage counters moved with them, re-keyed to (Store, Key).
		db.Usage.Count().Should().Be(3);
		db.Usage.Where(u => u.Store == "ops").Select(u => u.DeliberateCount).ToList().Should().BeEquivalentTo([2L]);

		// Search docs: the entity Type is rewritten from the old constant "memory" to the STORE,
		// which is how a store is addressed in the shared index post-merge.
		Scalar(db, "SELECT count(*) FROM search_fts WHERE Type = 'memory'").Should().Be(0);
		Scalar(db, "SELECT count(*) FROM search_fts WHERE Type = 'notes'").Should().Be(2);
		Scalar(db, "SELECT count(*) FROM search_vec WHERE Type = 'ops'").Should().Be(1);
		// Vector cursors are namespaced per store; the jobs' own markers ride along verbatim.
		Scalar(db, $"SELECT Version FROM search_cursor WHERE IndexName = '{MemoryCursors.Vector("notes")}'").Should().Be(2);
		Scalar(db, $"SELECT Version FROM search_cursor WHERE IndexName = '{MemoryCursors.Vector("ops")}'").Should().Be(1);
		Scalar(db, "SELECT Version FROM search_cursor WHERE IndexName = 'behavior-mining'").Should().Be(42);
		Scalar(db, "SELECT count(*) FROM search_deadletter WHERE Type = 'notes' AND Dead = 1").Should().Be(1);

		// The verified progress log — one row per merged store, carrying the copied counts.
		Scalar(db, "SELECT count(*) FROM memory_store_merge").Should().Be(2);
		Scalar(db, "SELECT EntryRows FROM memory_store_merge WHERE Store = 'notes'").Should().Be(2);

		// The legacy files are LEFT ON DISK on purpose (deletion is a separate later release).
		File.Exists(Path.Combine(_memoryDir, Proj, "notes.db")).Should().BeTrue();
	}

	[Fact]
	public void Merge_IsIdempotent_ReRunDuplicatesNothing()
	{
		SeedLegacyStore("notes", ("n1", "alpha body"), ("n2", "beta body"));
		using (var _ = _factory.NewEnsuredConnection(Proj)) { }

		// A second (and third) merge pass over the same file — the legacy files are still there,
		// so this is exactly what a re-run of the migration would do.
		LegacyStoreMerge.Run(ProjectDbPath);
		LegacyStoreMerge.Run(ProjectDbPath);

		using var db = _factory.NewEnsuredConnection(Proj);
		db.Entries.Count().Should().Be(2);
		db.Usage.Count().Should().Be(2);
		Scalar(db, "SELECT count(*) FROM search_fts").Should().Be(2);
		Scalar(db, "SELECT count(*) FROM search_vec").Should().Be(2);
		Scalar(db, "SELECT count(*) FROM memory_store_merge").Should().Be(1);
	}

	[Fact]
	public void Merge_IsResumable_FinishesTheStoresAnInterruptedRunMissed()
	{
		SeedLegacyStore("notes", ("n1", "alpha body"));
		SeedLegacyStore("ops", ("o1", "gamma body"));
		SeedLegacyStore("canon", ("c1", "delta body"));
		using (var _ = _factory.NewEnsuredConnection(Proj)) { }

		// Simulate an interruption after 1 of 3 stores: wipe every trace of the other two, as a
		// rolled-back per-store transaction would have left the file.
		using (var db = _factory.NewEnsuredConnection(Proj))
		{
			db.Execute("DELETE FROM memory_entries WHERE Store IN ('ops','canon')");
			db.Execute("DELETE FROM entry_usage WHERE Store IN ('ops','canon')");
			db.Execute("DELETE FROM search_fts WHERE Type IN ('ops','canon')");
			db.Execute("DELETE FROM memory_store_merge WHERE Store IN ('ops','canon')");
		}

		LegacyStoreMerge.Run(ProjectDbPath); // the re-run

		using var check = _factory.NewEnsuredConnection(Proj);
		check.Entries.Select(e => e.Store).Distinct().ToList()
			.Should().BeEquivalentTo(["notes", "ops", "canon"]);
		check.Entries.Count().Should().Be(3); // the already-merged store was NOT copied twice
		Scalar(check, "SELECT count(*) FROM memory_store_merge").Should().Be(3);
	}

	[Fact]
	public async Task Merge_MergedEntries_AreReachableThroughTheService()
	{
		SeedLegacyStore("notes", ("n1", "alpha body"));
		// The catalog (core.db) is untouched by the merge — it already knows the store in prod.
		await _store.CreateAsync(Proj, "notes", null);

		var memory = new MemoryService(_store);
		var entries = await memory.ListAsync(Proj, "notes", null);
		entries.Should().ContainSingle().Which.Key.Should().Be("n1");

		// And a write on top of the merged rows continues that store's version space.
		var r = await memory.UpsertAsync(Proj, "notes",
			[new PetBox.Memory.Contract.MemoryEntryInput { Key = "n2", Type = "Project", Body = "new" }], []);
		r.Result.Applied.Should().BeTrue();
		r.Result.CurrentVersion.Should().BeGreaterThan(1); // past the merged rows' versions
	}
}
