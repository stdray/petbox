using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

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

	static void ClearPoolsFor(string dbPath)
	{
		// Pool identity is the connection string; cover the spellings in use.
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath}"));
		SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dbPath};Cache=Shared"));
	}
}
