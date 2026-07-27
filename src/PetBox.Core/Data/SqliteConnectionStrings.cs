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
	}

	static bool Has(string connectionString, string keyword) =>
		connectionString.Contains(keyword, StringComparison.OrdinalIgnoreCase);

	static string Append(string connectionString, string keyValue) =>
		connectionString.TrimEnd(';') + ";" + keyValue;
}
