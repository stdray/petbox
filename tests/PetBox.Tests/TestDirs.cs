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

	// Mid-test handle release (e.g. before a file rename) without touching foreign pools.
	public static void ClearPoolsUnder(string dir)
	{
		foreach (var db in Directory.EnumerateFiles(dir, "*.db", SearchOption.AllDirectories))
			ClearPoolsFor(db);
	}

	static void ClearPoolsFor(string dbPath)
	{
		// Pool identity is the connection string, so this has to name every spelling production
		// can open the file with. That list used to be typed out here by hand, and the combined
		// `;Cache=Shared;Foreign Keys=True` one — PetBoxDb.CreateOptions' output for core.db — was
		// missing from it: for twelve test classes nothing was ever cleared and the temp dir
		// stayed locked, silently. It is now DERIVED from the production spelling functions
		// (SqliteConnectionStrings), so a change to either decoration reaches teardown for free;
		// SqliteConnectionStringSpellingTests fails if a context ever produces a spelling the
		// derivation does not cover.
		foreach (var connectionString in SqliteConnectionStrings.Spellings(dbPath))
			SqliteConnection.ClearPool(new SqliteConnection(connectionString));
	}
}
