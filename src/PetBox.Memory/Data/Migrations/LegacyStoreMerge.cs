using Microsoft.Data.Sqlite;

namespace PetBox.Memory.Data.Migrations;

// The one-time data move behind M010: pull every legacy per-store file
// `memory/{project}/{store}.db` into the merged per-project file `memory/{project}.db`, stamping
// the source file's NAME into the new `Store` column (and into the search tables' entity `Type`,
// which is how a store is addressed post-merge — see M009's note).
//
// Runs on its OWN connection (not FluentMigrator's), so each store commits INDEPENDENTLY: an
// interrupted run keeps the stores it already finished and a re-run picks up the rest. Progress
// is recorded in `memory_store_merge` (created by M009) with the VERIFIED row counts — a store is
// only logged after its copied row count is confirmed equal to the source's.
//
// Idempotence: a store already present in `memory_store_merge` is skipped; an unlogged store's
// rows are cleared before the copy (so a torn write can never duplicate). Copy + verify + log all
// ride ONE transaction per store, so a store is either fully in and logged, or fully absent.
//
// The legacy files are DELIBERATELY LEFT ON DISK. Deleting them ships as a SEPARATE, LATER release
// once the merged layout is verified on live data — an unreadable rollback is not a rollback.
public static class LegacyStoreMerge
{
	// projectDbPath: the merged file, `{baseDir}/{project}.db`. The legacy stores of the same
	// project live in the sibling directory `{baseDir}/{project}/`.
	public static void Run(string projectDbPath)
	{
		var baseDir = Path.GetDirectoryName(projectDbPath);
		var project = Path.GetFileNameWithoutExtension(projectDbPath);
		if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(project)) return;

		var legacyDir = Path.Combine(baseDir, project);
		if (!Directory.Exists(legacyDir)) return; // nothing to merge — the common (fresh) case

		var legacyFiles = Directory.GetFiles(legacyDir, "*.db");
		if (legacyFiles.Length == 0) return;

		using var db = new SqliteConnection($"Data Source={projectDbPath}");
		db.Open();
		Exec(db, $"PRAGMA busy_timeout = {Core.Data.SqlitePragmas.DefaultBusyTimeoutMs};");

		var done = MergedStores(db);
		foreach (var file in legacyFiles.OrderBy(f => f, StringComparer.Ordinal))
		{
			var store = Path.GetFileNameWithoutExtension(file);
			if (string.IsNullOrEmpty(store) || done.Contains(store)) continue;
			MergeOne(db, file, store);
		}
	}

	static HashSet<string> MergedStores(SqliteConnection db)
	{
		var set = new HashSet<string>(StringComparer.Ordinal);
		using var cmd = db.CreateCommand();
		cmd.CommandText = "SELECT Store FROM memory_store_merge;";
		using var r = cmd.ExecuteReader();
		while (r.Read()) set.Add(r.GetString(0));
		return set;
	}

	// One store, one transaction: clear any partial rows, copy, VERIFY the counts, log. ATTACH must
	// run outside a transaction, hence the attach/detach around the tx.
	static void MergeOne(SqliteConnection db, string legacyFile, string store)
	{
		Exec(db, "ATTACH DATABASE @f AS legacy;", ("@f", legacyFile));
		try
		{
			var entryCols = Columns(db, "legacy", "memory_entries");
			if (entryCols.Count == 0) return; // not a memory store file (or empty shell) — leave it alone

			using var tx = db.BeginTransaction();

			// A torn previous attempt cannot leave rows behind (copy+log share this tx), but clear
			// anyway: it makes the merge safe to re-drive by hand after any surgery.
			foreach (var t in new[] { "memory_entries", "entry_usage" })
				Exec(db, $"DELETE FROM {t} WHERE Store = @s;", tx, ("@s", store));
			foreach (var t in new[] { "search_fts", "search_vec" })
				Exec(db, $"DELETE FROM {t} WHERE Type = @s;", tx, ("@s", store));
			Exec(db, "DELETE FROM search_deadletter WHERE Type = @s;", tx, ("@s", store));
			Exec(db, "DELETE FROM search_cursor WHERE IndexName = @ix;", tx, ("@ix", MemoryCursors.Vector(store)));

			// memory_entries — the payload. Type/Metadata landed in M002/M004; a legacy file that
			// somehow predates them contributes the migration defaults instead of failing the merge.
			var type = entryCols.Contains("Type") ? "Type" : "2";
			var meta = entryCols.Contains("Metadata") ? "Metadata" : "''";
			var entryRows = Exec(db, $"""
				INSERT INTO memory_entries (Store,Key,Version,Type,Description,Body,Tags,Metadata,PrevKey,ActiveFrom,ActiveTo,Created,Updated)
				SELECT @s, Key, Version, {type}, Description, Body, Tags, {meta}, PrevKey, ActiveFrom, ActiveTo, Created, Updated
				FROM legacy.memory_entries;
				""", tx, ("@s", store));

			// entry_usage — telemetry counters (DeliberateCount landed in M008).
			var usageRows = 0;
			var usageCols = Columns(db, "legacy", "entry_usage");
			if (usageCols.Count > 0)
			{
				var deliberate = usageCols.Contains("DeliberateCount") ? "DeliberateCount" : "0";
				usageRows = Exec(db, $"""
					INSERT INTO entry_usage (Store,Key,SurfacedCount,DeliberateCount,OpenedCount,LastHitAt)
					SELECT @s, Key, SurfacedCount, {deliberate}, OpenedCount, LastHitAt FROM legacy.entry_usage;
					""", tx, ("@s", store));
			}

			// Search state. The lexical rows carry TokenStemmer shadow terms that only C# can
			// produce, so they are COPIED rather than rebuilt (a lost search_fts row would silently
			// cost recall until the store's next write); the entity Type is rewritten to the store.
			// Vectors + their cursor/dead-letter move too, so the merge costs no re-embedding.
			if (Columns(db, "legacy", "search_fts").Count > 0)
				Exec(db, """
					INSERT INTO search_fts (Scope,Type,Id,Text,Tags)
					SELECT Scope, @s, Id, Text, Tags FROM legacy.search_fts;
					""", tx, ("@s", store));
			if (Columns(db, "legacy", "search_vec").Count > 0)
				Exec(db, """
					INSERT INTO search_vec (Scope,Type,Id,Model,Dim,Vec)
					SELECT Scope, @s, Id, Model, Dim, Vec FROM legacy.search_vec;
					""", tx, ("@s", store));
			if (Columns(db, "legacy", "search_cursor").Count > 0)
			{
				Exec(db, """
					INSERT INTO search_cursor (IndexName, Version)
					SELECT @ix, Version FROM legacy.search_cursor WHERE IndexName = @old;
					""", tx, ("@ix", MemoryCursors.Vector(store)), ("@old", MemoryCursors.LegacyVector));
				// The background jobs keep their own markers here too (behavior-mining, dedup
				// sweep — only ever in the `autocaptured` store, so their names cannot collide
				// across the merged stores). Carried over verbatim: dropping them would re-trigger
				// a mining pass / dedup sweep for no reason.
				Exec(db, """
					INSERT OR IGNORE INTO search_cursor (IndexName, Version)
					SELECT IndexName, Version FROM legacy.search_cursor WHERE IndexName <> @old;
					""", tx, ("@old", MemoryCursors.LegacyVector));
			}
			if (Columns(db, "legacy", "search_deadletter").Count > 0)
				Exec(db, """
					INSERT INTO search_deadletter (IndexName, Type, Id, Attempts, Dead)
					SELECT @ix, @s, Id, Attempts, Dead FROM legacy.search_deadletter WHERE IndexName = @old;
					""", tx, ("@ix", MemoryCursors.Vector(store)), ("@s", store), ("@old", MemoryCursors.LegacyVector));

			// VERIFY before the store counts as done: every source row must have landed. A mismatch
			// throws → the tx rolls back → the store stays unlogged and the next run retries it.
			var srcEntries = Scalar(db, "SELECT count(*) FROM legacy.memory_entries;", tx);
			var dstEntries = Scalar(db, "SELECT count(*) FROM memory_entries WHERE Store = @s;", tx, ("@s", store));
			if (srcEntries != dstEntries)
				throw new InvalidOperationException(
					$"memory store merge for '{store}': copied {dstEntries} of {srcEntries} memory_entries rows — aborted, will retry");
			if (usageCols.Count > 0)
			{
				var srcUsage = Scalar(db, "SELECT count(*) FROM legacy.entry_usage;", tx);
				var dstUsage = Scalar(db, "SELECT count(*) FROM entry_usage WHERE Store = @s;", tx, ("@s", store));
				if (srcUsage != dstUsage)
					throw new InvalidOperationException(
						$"memory store merge for '{store}': copied {dstUsage} of {srcUsage} entry_usage rows — aborted, will retry");
			}

			Exec(db, """
				INSERT INTO memory_store_merge (Store, EntryRows, UsageRows, MergedAt)
				VALUES (@s, @e, @u, @at);
				""", tx, ("@s", store), ("@e", entryRows), ("@u", usageRows),
				("@at", DateTime.UtcNow.ToString("O")));

			tx.Commit();
		}
		finally
		{
			Exec(db, "DETACH DATABASE legacy;");
		}
	}

	// Column names of a table, or empty when the table does not exist (a legacy file at an older
	// migration level, or a non-memory `.db` that wandered into the directory).
	static HashSet<string> Columns(SqliteConnection db, string schema, string table)
	{
		var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		using var cmd = db.CreateCommand();
		cmd.CommandText = $"PRAGMA {schema}.table_info({table});";
		using var r = cmd.ExecuteReader();
		while (r.Read()) cols.Add(r.GetString(1));
		return cols;
	}

	static long Scalar(SqliteConnection db, string sql, SqliteTransaction? tx = null, params (string, object)[] ps)
	{
		using var cmd = db.CreateCommand();
		cmd.CommandText = sql;
		cmd.Transaction = tx;
		foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
		return Convert.ToInt64(cmd.ExecuteScalar());
	}

	static int Exec(SqliteConnection db, string sql, params (string, object)[] ps) => Exec(db, sql, null, ps);

	static int Exec(SqliteConnection db, string sql, SqliteTransaction? tx, params (string, object)[] ps)
	{
		using var cmd = db.CreateCommand();
		cmd.CommandText = sql;
		cmd.Transaction = tx;
		foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
		return cmd.ExecuteNonQuery();
	}
}
