using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;

namespace PetBox.Tests;

// Parallel-safe teardown for per-test temp dirs holding SQLite files. The old pattern —
// SqliteConnection.ClearAllPools() in every Dispose — is process-global: under parallel
// test classes one class's teardown yanks pooled connections out from under every other
// in-flight test, which is why the suite used to be serialized into a single collection.
// Here we clear only the pools of the databases under the caller's own temp dir (pools
// are keyed by connection string, so we cover the spellings tests and factories actually
// use), and when Windows still holds a handle we defer the delete to process exit instead
// of failing the test.
public static class TestDirs
{
	static readonly ConcurrentQueue<string> Deferred = new();

	static TestDirs()
	{
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			// The run is over — a global pool clear can no longer hurt anyone.
			SqliteConnection.ClearAllPools();
			while (Deferred.TryDequeue(out var dir))
			{
				try { Directory.Delete(dir, recursive: true); }
				catch { /* best effort — the OS temp cleaner picks up stragglers */ }
			}
		};
	}

	public static void CleanupOrDefer(string dir)
	{
		if (!Directory.Exists(dir)) return;
		ClearPoolsUnder(dir);
		try { Directory.Delete(dir, recursive: true); }
		catch (IOException) { Deferred.Enqueue(dir); }
		catch (UnauthorizedAccessException) { Deferred.Enqueue(dir); }
	}

	// Also usable for a single DB file living outside a per-class dir.
	public static void CleanupOrDefer(FileInfo dbFile)
	{
		if (!dbFile.Exists) return;
		ClearPoolsFor(dbFile.FullName);
		try { dbFile.Delete(); }
		catch (IOException) { /* handle still pooled elsewhere — temp cleaner's problem */ }
		catch (UnauthorizedAccessException) { }
	}

	// Mid-test handle release (e.g. before a file rename) without touching foreign pools.
	public static void ClearPoolsUnder(string dir)
	{
		foreach (var db in Directory.EnumerateFiles(dir, "*.db", SearchOption.AllDirectories))
			ClearPoolsFor(db);
	}

	// Per-test reset of one SQLite file: drop the pooled handles, fold the WAL back into the
	// main file so no -wal/-shm sidecar survives, then delete.
	//
	// The ORDER is the whole point, and it is not obvious: wal_checkpoint(TRUNCATE) takes an
	// EXCLUSIVE lock. Run it while pooled connections to the same file are still open and it
	// does not fail fast — it blocks for the full 30s default busy timeout, and the batched
	// ExecuteNonQuery then SWALLOWS the resulting SQLITE_BUSY. The tests still pass; they just
	// get 30 seconds slower each, invisibly. Two fixtures independently grew that bug by
	// copying the block in the wrong order, and it cost the suite ~200s of wall clock before
	// anyone measured it. Clear the pools FIRST. Call this instead of open-coding the sequence.
	public static void ResetDbFile(string path)
	{
		ClearPoolsFor(path);
		if (File.Exists(path))
		{
			using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
			cmd.ExecuteNonQuery();
		}
		// A single-shot delete is a race under load: the previous test's server-side request
		// scope may still be releasing its connection as the next test starts.
		for (var attempt = 0; attempt < 5; attempt++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			if (ScopedDbFiles.TryDelete(path))
				return;
			Thread.Sleep(100);
		}
		throw new InvalidOperationException($"per-test reset could not delete {path} (still locked)");
	}

	static void ClearPoolsFor(string dbPath)
	{
		// Pool identity is the connection string; cover the spellings in use.
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath};Cache=Shared"));
		// TasksDb.CreateOptions appends this one — without it the tasks pools survive a clear.
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath};Foreign Keys=True"));
	}
}
