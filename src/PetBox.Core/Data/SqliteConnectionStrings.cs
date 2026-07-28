namespace PetBox.Core.Data;

// THE spelling of a PetBox SQLite connection string. Every decoration production appends on top
// of a bare `Data Source={path}` is defined here ONCE, and `Spellings` is DERIVED from those same
// functions rather than restating their text.
//
// Why that matters beyond tidiness: Microsoft.Data.Sqlite keys its connection POOL by the
// connection string, so "release the pooled handles for this file" means "clear one pool per
// spelling in use". The test teardown (tests/PetBox.Tests/TestDirs.cs) used to re-type that list
// by hand, and the combined `;Cache=Shared;Foreign Keys=True` spelling — the one
// PetBoxDb.CreateOptions produces for core.db, because config already supplies Cache=Shared — was
// missing from it. Nothing failed loudly: for twelve test classes the pools were simply never
// cleared and the temp dir stayed locked. A hand-maintained copy of a spelling is a copy that
// silently falls behind the original; SqliteConnectionStringSpellingTests pins the two together.
public static class SqliteConnectionStrings
{
	public static string ForFile(string path) => $"Data Source={path}";

	// Cross-connection shared cache. core.db and deploy.db run with it (it comes from
	// configuration, not from here) — read the SQLITE_LOCKED note in AGENTS.md before adding a
	// third.
	public static string WithSharedCache(string connectionString) =>
		Has(connectionString, "Cache=") ? connectionString : Append(connectionString, "Cache=Shared");

	// Per-connection FK enforcement — SQLite defaults it OFF, and an unenforced FK is decoration.
	// PetBoxDb.CreateOptions and TasksDb.CreateOptions are its only callers, and they must stay
	// its only callers or `Spellings` below stops being complete.
	public static string WithForeignKeys(string connectionString) =>
		Has(connectionString, "Foreign Keys") ? connectionString : Append(connectionString, "Foreign Keys=True");

	// ADO.NET's command-timeout seconds, as a connection-string keyword. This is NOT a synonym for
	// `PRAGMA busy_timeout` and the difference was measured, not assumed: Microsoft.Data.Sqlite wraps
	// every command in its OWN managed retry loop keyed to SqliteConnection.DefaultTimeout, so against
	// a busy writer the PRAGMA alone does not bound the wait at all — a diagnostic at 200/1000/2000/
	// 5000 ms PRAGMA values blocked ~30-34 s in EVERY case, i.e. the ADO.NET default (30 s) was the
	// real ceiling throughout. `Default Timeout=` is the knob that actually moves it (confirmed at 1 s
	// and 3 s giving ~1.1 s / ~3.1 s failures). Only the cache file uses it — see SqliteDistributedCache,
	// which must fail FAST and degrade to a miss rather than hold a request behind a 30-second stall.
	public const int DefaultTimeoutSeconds = SqlitePragmas.DefaultBusyTimeoutMs / 1000;

	public static string WithDefaultTimeout(string connectionString) =>
		Has(connectionString, "Default Timeout")
			? connectionString
			: Append(connectionString, $"Default Timeout={DefaultTimeoutSeconds}");

	// Every connection string a pooled connection to `path` can carry: the cross product of the
	// decorations above, each produced by APPLYING the production function rather than by
	// repeating what it writes.
	public static IEnumerable<string> Spellings(string path)
	{
		foreach (var cs in new[] { ForFile(path), WithSharedCache(ForFile(path)) })
		{
			yield return cs;
			yield return WithForeignKeys(cs);
		}

		// NOT part of the cross product above, because production never crosses it with anything: the
		// one file that carries a Default Timeout is the cache, which is deliberately never
		// shared-cache and has no foreign keys. Listing the four unreachable combinations would make
		// this derivation a superset of what production can open, and the point of the list is that it
		// says what production DOES.
		yield return WithDefaultTimeout(ForFile(path));
	}

	static bool Has(string connectionString, string keyword) =>
		connectionString.Contains(keyword, StringComparison.OrdinalIgnoreCase);

	static string Append(string connectionString, string keyValue) =>
		connectionString.TrimEnd(';') + ";" + keyValue;
}
