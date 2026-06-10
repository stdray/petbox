using LinqToDB.Data;
using PetBox.Core.Settings;

namespace PetBox.Core.Data;

// A scope-keyed SQLite database factory: maps a (scopeKey [, name]) pair to a
// cached, lazily-schema'd linq2db context. Generalises the three near-identical
// per-scope factories (logs, config) that all shared the same
// lock + Dictionary cache + create-schema-on-first-open shape.
//
//   logs   = ScopedDbFactory<LogDb>("logs", Scope.Project, ...)   -> logs/{project}/{log}.db
//   config = ScopedDbFactory<ConfigDb>("config", Scope.Workspace, ...) -> config/{ws}.db
//
// DataDbFactory stays separate: user-data scales to many DBs and owns its own
// schema, so it hands out connection strings instead of cached contexts.
public interface IScopedDbFactory<TContext> : IAsyncDisposable
	where TContext : DataConnection
{
	// The scope this factory is bound to (documentation/validation for callers).
	Scope Scope { get; }

	// Root directory under which this factory's `.db` files live.
	string BaseDir { get; }

	// Resolves the context for the given scope key (+ optional sub-name). Creates
	// the file and schema on first access, then caches the context.
	TContext GetDb(string scopeKey, string? name = null);

	// Opens a FRESH, caller-owned connection to an EXISTING scope file — the caller
	// disposes it. Unlike GetDb this is never cached and does NOT re-run schema (the
	// file must already exist, i.e. GetDb/CreateAsync ran for it before). Used by the
	// search indexes, whose reads do `using var db = connect()` (they dispose the
	// connection), and by the async-vectorization worker, which needs its own connection
	// off the request-scoped cache. WAL is persisted in the file; SQLITE_BUSY is handled
	// by Microsoft.Data.Sqlite's command timeout — same as the cached connection.
	TContext NewConnection(string scopeKey, string? name = null);

	// Disposes and removes the cached context for (scopeKey [, name]) so the
	// underlying file is no longer held open. Required before deleting the file
	// (a cached connection would keep it locked on Windows). No-op if not cached.
	ValueTask EvictAsync(string scopeKey, string? name = null);
}

public sealed class ScopedDbFactory<TContext> : IScopedDbFactory<TContext>
	where TContext : DataConnection
{
	readonly string _baseDir;
	readonly Func<string, TContext> _create;
	readonly Action<string> _ensureSchema;
	readonly Dictionary<string, TContext> _cache = [];
	readonly object _lock = new();

	public ScopedDbFactory(
		string baseDir,
		Scope scope,
		Func<string, TContext> create,
		Action<string> ensureSchema)
	{
		_baseDir = baseDir;
		Scope = scope;
		_create = create;
		_ensureSchema = ensureSchema;
		Directory.CreateDirectory(_baseDir);
	}

	public Scope Scope { get; }

	public string BaseDir => _baseDir;

	public TContext GetDb(string scopeKey, string? name = null)
	{
		var cacheKey = name is null ? scopeKey : $"{scopeKey}/{name}";
		lock (_lock)
		{
			if (_cache.TryGetValue(cacheKey, out var existing))
				return existing;

			var dbPath = ScopedDbFiles.PathFor(_baseDir, scopeKey, name);
			var dir = Path.GetDirectoryName(dbPath);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			var cs = $"Data Source={dbPath}";
			_ensureSchema(cs);

			var db = _create(cs);
			_cache[cacheKey] = db;
			return db;
		}
	}

	public TContext NewConnection(string scopeKey, string? name = null)
	{
		var dbPath = ScopedDbFiles.PathFor(_baseDir, scopeKey, name);
		return _create($"Data Source={dbPath}");
	}

	public async ValueTask EvictAsync(string scopeKey, string? name = null)
	{
		var cacheKey = name is null ? scopeKey : $"{scopeKey}/{name}";
		TContext? db;
		lock (_lock)
		{
			if (!_cache.Remove(cacheKey, out db))
				return;
		}
		await db.DisposeAsync();
	}

	public async ValueTask DisposeAsync()
	{
		List<TContext> toDispose;
		lock (_lock)
		{
			toDispose = [.. _cache.Values];
			_cache.Clear();
		}
		foreach (var db in toDispose)
			await db.DisposeAsync();
	}
}
