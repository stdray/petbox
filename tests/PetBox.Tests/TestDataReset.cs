using LinqToDB.Data;

namespace PetBox.Tests;

// Per-test DATA reset for a shared per-class fixture (work share-fixtures-across-per-test-classes,
// wave 2): wipes every row in an already-migrated file, leaving the schema (tables, indexes,
// triggers, the FluentMigrator VersionInfo row) untouched. This is the cheap alternative to
// TestDirs.ResetDbFile, which clears connection pools, WAL-checkpoints and DELETEs the file —
// measured 176-559ms per call, more than the 1-14ms a fresh templated copy costs (see
// TestSchema.Templated). A DELETE FROM per table on a handful of rows is on the order of a
// millisecond, so this reset is cheap enough to run before every [Fact] in a shared-fixture class.
//
// Generic over sqlite_master rather than a hand-maintained table list on purpose: a per-file
// table list drifts the moment a migration adds one (comment_tag/node_tag/methodology_* were all
// added after the tier's first table), and a stale list would silently leak state instead of
// failing loudly. FTS5 virtual tables (search_fts) are deleted through the virtual table itself —
// never through their _data/_idx/_docsize/_config shadow tables directly, which would desync the
// index from its own bookkeeping. `Foreign Keys` is off for the sweep (TasksDb turns it on by
// default; node_tag -> tag_vocab and relations -> plan_node_ids would otherwise dictate a delete
// order) and restored before the connection is handed back — SQLite triggers are independent of
// the foreign_keys pragma, so plan_nodes' own register/unregister-id triggers still fire and keep
// plan_node_ids (and, via its FK, relations) in sync with the wipe.
public static class TestDataReset
{
	public static void WipeAllTables(DataConnection db, params string[] except)
	{
		var skip = new HashSet<string>(except, StringComparer.OrdinalIgnoreCase) { "VersionInfo" };

		var tables = db.Query<string>(
			"SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'").ToList();
		var virtualTables = db.Query<string>(
				"SELECT name FROM sqlite_master WHERE type = 'table' AND sql LIKE 'CREATE VIRTUAL TABLE%'")
			.ToList();
		var shadowPrefixes = virtualTables.Select(v => v + "_").ToList();

		db.Execute("PRAGMA foreign_keys = OFF;");
		try
		{
			foreach (var table in tables)
			{
				if (skip.Contains(table)) continue;
				if (shadowPrefixes.Any(p => table.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
				db.Execute($"DELETE FROM \"{table}\";");
			}
		}
		finally
		{
			db.Execute("PRAGMA foreign_keys = ON;");
		}
	}
}
