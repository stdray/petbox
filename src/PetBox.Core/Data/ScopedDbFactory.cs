using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Settings;

namespace PetBox.Core.Data;

// A scope-keyed SQLite database factory: maps a (scopeKey [, name]) pair to a
// FRESH, caller-owned, schema-ensured linq2db connection. Schema runs exactly once
// per file (flag+lock serializes the first caller, later callers skip the ensure).
// The caller disposes the connection.
//
//   logs   = ScopedDbFactory<LogDb>("logs", Scope.Project, ...)   -> logs/{project}/{log}.db
//   config = ScopedDbFactory<ConfigDb>("config", Scope.Workspace, ...) -> config/{ws}.db
//
// DataDbFactory stays separate: user-data scales to many DBs and owns its own
// schema, so it hands out connection strings instead of connections.
public interface IScopedDbFactory<TContext> : IAsyncDisposable
	where TContext : DataConnection
{
	// The scope this factory is bound to (documentation/validation for callers).
	Scope Scope { get; }

	// Root directory under which this factory's `.db` files live.
	string BaseDir { get; }

	// Returns a fresh, caller-owned, schema-ensured connection (no longer cached).
	// The caller disposes it.
	TContext GetDb(string scopeKey, string? name = null);

	// Ensures the file schema on first call per (scopeKey, name), then returns a
	// fresh caller-owned connection. The caller disposes it.
	TContext NewEnsuredConnection(string scopeKey, string? name = null);

	// Removes the ensure-flag for (scopeKey [, name]) so a future call re-runs
	// schema (e.g. after deleting and recreating the file).
	ValueTask EvictAsync(string scopeKey, string? name = null);
}

public sealed class ScopedDbFactory<TContext> : IScopedDbFactory<TContext>
	where TContext : DataConnection
{
	readonly string _baseDir;
	readonly Func<string, TContext> _create;
	readonly Action<string> _ensureSchema;
	readonly Dictionary<string, bool> _ensured = [];
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

	public TContext GetDb(string scopeKey, string? name = null) =>
		NewEnsuredConnection(scopeKey, name);

	public TContext NewEnsuredConnection(string scopeKey, string? name = null)
	{
		var cacheKey = name is null ? scopeKey : $"{scopeKey}/{name}";
		var dbPath = ScopedDbFiles.PathFor(_baseDir, scopeKey, name);
		var dir = Path.GetDirectoryName(dbPath);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);
		var cs = $"Data Source={dbPath}";

		// Flag+lock serializes ONLY the first DDL per file — without the flag two threads
		// both see "not migrated" and race on the FluentMigrator journal table. After the
		// first caller runs the schema, every later caller skips the lock and creates a
		// fresh connection lock-free. Removing the flag reintroduces the race.
		lock (_lock)
		{
			if (!_ensured.TryGetValue(cacheKey, out _))
			{
				_ensureSchema(cs);
				_ensured[cacheKey] = true;
			}
		}

		return _create(cs);
	}

	public async ValueTask EvictAsync(string scopeKey, string? name = null)
	{
		var cacheKey = name is null ? scopeKey : $"{scopeKey}/{name}";
		lock (_lock)
		{
			_ensured.Remove(cacheKey);
		}
		await Task.CompletedTask;
	}

	public ValueTask DisposeAsync()
	{
		lock (_lock)
			_ensured.Clear();
		return default;
	}
}
