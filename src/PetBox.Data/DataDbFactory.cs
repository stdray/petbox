using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace PetBox.Data;

// Per-(project, dbName) SQLite database factory for the user-data Data module.
// Mirrors LogDbFactory: physical file lives at `{baseDir}/{projectKey}/{dbName}.db`,
// connection strings are SQLite, schema is owned by the pet (petbox doesn't
// CREATE TABLE — that's what the /schema endpoint is for).
//
// On first creation of a `.db` file we apply two operational PRAGMAs:
//   - journal_mode = WAL (allows readers concurrent with a writer)
//   - max_page_count = N (per-DB size quota; INSERT over the limit → SQLITE_FULL)
//
// These two are NOT alike, and the difference used to be a production bug:
//   - journal_mode IS persistent — it's written into the DB file header, so every
//     later connection to the file opens in WAL. Setting it once at create is enough.
//   - max_page_count is NOT persistent — it is per-CONNECTION state, reset to the
//     SQLite default (4294967294 pages) on every fresh open. Setting it once at
//     create quota'd exactly one throwaway connection. Every later connection
//     (fresh, or handed back by the pool after the create-time one was pruned) had
//     NO quota, so a project could write past its limit and fill the disk.
// The quota value therefore lives in DataDbs.MaxPageCount and MUST be re-applied on
// EVERY open — that's what OpenAsync is for. Do not open a data DB with a bare
// `new SqliteConnection(GetConnectionString(...))` on any write path.
//
// petbox opens fresh connections per HTTP request, since SqliteConnection has a
// built-in pool. A separate hosted service runs PRAGMA wal_checkpoint(TRUNCATE)
// periodically to keep the .wal sidecar bounded.
public interface IDataDbFactory
{
	// Returns a SQLite connection string for the given (projectKey, dbName).
	// If the file does not exist, throws — use CreateAsync first.
	// NOTE: a connection opened from this string carries NO size quota. Prefer
	// OpenAsync for anything that can write.
	string GetConnectionString(string projectKey, string dbName);

	// Opens a connection to (projectKey, dbName) and applies the per-connection
	// size quota (PRAGMA max_page_count = maxPageCount) before returning it.
	// `maxPageCount` comes from the DataDbs row — the caller has it already.
	// The caller owns the returned connection and must dispose it.
	Task<SqliteConnection> OpenAsync(string projectKey, string dbName, long maxPageCount, CancellationToken ct = default);

	// Creates a new SQLite file at the resolved path, applies WAL +
	// max_page_count. Throws if the file already exists.
	Task CreateAsync(string projectKey, string dbName, long maxPageCount, CancellationToken ct = default);

	// Deletes the file for (projectKey, dbName) along with .wal / .shm sidecars.
	// Best-effort: if the file is currently open by another connection (Windows
	// file lock), returns false and the orphan-cleanup service retries later.
	bool TryDelete(string projectKey, string dbName);

	// Enumerates `.db` files on disk for a project. Used by the orphan-cleanup
	// service to compare against DataDbs metadata.
	IReadOnlyList<string> ListPhysicalDbs(string projectKey);

	// Enumerates project subdirectories under baseDir. Used by orphan cleanup
	// to find projects that no longer have any DataDbs metadata rows but still
	// have leftover files on disk.
	IReadOnlyList<string> ListProjectDirectories();

	// Resolved on-disk path for diagnostics.
	string GetDbPath(string projectKey, string dbName);
}

public sealed class DataDbFactory : IDataDbFactory
{
	public const long DefaultMaxPageCount = 262144; // ~1 GB at 4 KB page size

	readonly string _baseDir;

	public DataDbFactory(string baseDir)
	{
		_baseDir = baseDir;
		Directory.CreateDirectory(_baseDir);
	}

	public string GetDbPath(string projectKey, string dbName) =>
		Path.Combine(_baseDir, projectKey, $"{dbName}.db");

	public string GetConnectionString(string projectKey, string dbName)
	{
		var path = GetDbPath(projectKey, dbName);
		if (!File.Exists(path))
			throw new FileNotFoundException($"DataDb file not found: {path}");
		return $"Data Source={path}";
	}

	public async Task<SqliteConnection> OpenAsync(string projectKey, string dbName, long maxPageCount, CancellationToken ct = default)
	{
		var conn = new SqliteConnection(GetConnectionString(projectKey, dbName));
		try
		{
			await conn.OpenAsync(ct);
			// Per-connection, not persisted in the file: re-apply on every open or the
			// quota silently does not exist for this connection.
			await using var pragma = conn.CreateCommand();
			pragma.CommandText = $"PRAGMA max_page_count = {maxPageCount};";
			await pragma.ExecuteNonQueryAsync(ct);
			return conn;
		}
		catch
		{
			await conn.DisposeAsync();
			throw;
		}
	}

	public async Task CreateAsync(string projectKey, string dbName, long maxPageCount, CancellationToken ct = default)
	{
		var projectDir = Path.Combine(_baseDir, projectKey);
		Directory.CreateDirectory(projectDir);

		var path = Path.Combine(projectDir, $"{dbName}.db");
		if (File.Exists(path))
			throw new InvalidOperationException($"DataDb already exists: {path}");

		var cs = $"Data Source={path}";
		await using var raw = new SqliteConnection(cs);
		await raw.OpenAsync(ct);

		await using (var pragma = raw.CreateCommand())
		{
			// PRAGMA journal_mode is a query that returns the new mode; using
			// ExecuteScalarAsync ensures it's actually applied and persisted.
			pragma.CommandText = "PRAGMA journal_mode = WAL;";
			await pragma.ExecuteScalarAsync(ct);
		}
		await using (var pragma = raw.CreateCommand())
		{
			// Applies to THIS connection only (see the interface comment): it caps the
			// create-time connection, nothing more. The durable copy of the quota is the
			// DataDbs.MaxPageCount row, re-applied by OpenAsync on every later open.
			pragma.CommandText = $"PRAGMA max_page_count = {maxPageCount};";
			await pragma.ExecuteNonQueryAsync(ct);
		}
	}

	public bool TryDelete(string projectKey, string dbName)
	{
		var path = GetDbPath(projectKey, dbName);
		// SQLite WAL mode produces -wal / -shm sidecars next to the main file.
		var sidecars = new[] { path, path + "-wal", path + "-shm" };

		// Try all three; if any can't be deleted (file locked, e.g. on Windows
		// during an in-flight query), bail out. The orphan-cleanup service
		// retries on its next tick.
		foreach (var f in sidecars)
		{
			if (!File.Exists(f)) continue;
			try { File.Delete(f); }
			catch (IOException) { return false; }
			catch (UnauthorizedAccessException) { return false; }
		}
		return true;
	}

	public IReadOnlyList<string> ListPhysicalDbs(string projectKey)
	{
		var projectDir = Path.Combine(_baseDir, projectKey);
		if (!Directory.Exists(projectDir)) return [];
		return Directory.GetFiles(projectDir, "*.db")
			.Select(p => Path.GetFileNameWithoutExtension(p))
			.ToList();
	}

	public IReadOnlyList<string> ListProjectDirectories()
	{
		if (!Directory.Exists(_baseDir)) return [];
		return Directory.GetDirectories(_baseDir)
			.Select(d => Path.GetFileName(d))
			.ToList();
	}
}
