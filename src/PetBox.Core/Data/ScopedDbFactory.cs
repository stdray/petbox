using System.Reflection;
using LinqToDB;
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

	// Bug: linq2db-per-connection-options-leak (prod OOM, 773k live interceptors / ~13h).
	// `_create` is always `cs => new XDb(XDb.CreateOptions(cs))` — one step that BOTH builds the
	// immutable DataOptions AND opens a fresh DataConnection. XDb.CreateOptions runs
	// SqliteDurability.WithDurability, which allocates a NEW LinqToDB.Interceptors.
	// ConnectionOptionsConnectionInterceptor on every call (the delegate it wraps is static and
	// harmless — the wrapper object is not). linq2db's static LinqToDB.Internal.Common.
	// IdentifierBuilder._objects interns that interceptor FOREVER (no eviction; ClearCache() is never
	// called anywhere in the codebase). Every context that rebuilt DataOptions on each
	// NewEnsuredConnection call therefore leaked one interceptor per connection opened.
	//
	// Fix: cache the built (non-generic) DataOptions per (scopeKey, name) so the SAME immutable
	// config object — interceptor included — backs every later connection to that file. Only the
	// DataOptions is shared; NewEnsuredConnection still returns a brand-new, caller-owned TContext
	// with its own ADO connection on every call (spec: conn-safety-fresh-conn). This is the same
	// shape as the already-healthy CoreDbFactory/CacheDbFactory/DeployDbFactory, which build
	// DataOptions once in their constructor and reuse it for every Open() — ScopedDbFactory just has
	// to do it per scope-key instead of once, since it is keyed by file rather than bound to one.
	//
	// Bounded by construction: the key space is the distinct (scopeKey, name) pairs this factory
	// instance is ever asked to open, i.e. the distinct .db files this context owns on disk — bounded
	// by how many project/workspace/log files exist (tens, not the millions of connections opened
	// against them), so it cannot grow with request or background-job volume the way the leaked
	// linq2db registries did.
	readonly Dictionary<string, DataOptions> _optionsCache = [];

	// Resolved once per TContext type, not per connection. Every context wired through this factory
	// declares exactly one public constructor shaped `TContext(DataOptions<TContext> options)` (see
	// LogDb, TasksDb, MemoryDb, SessionsDb, ConfigDb) — a generic constraint can't express "has this
	// constructor" (C# only supports the parameterless `new()`), so a single reflective lookup stands
	// in to rebuild a TContext from a cached DataOptions.
	static readonly ConstructorInfo _optionsCtor =
		typeof(TContext).GetConstructor([typeof(DataOptions<TContext>)])
		?? throw new InvalidOperationException(
			$"{typeof(TContext)} must declare a public constructor accepting DataOptions<{typeof(TContext).Name}> " +
			"for ScopedDbFactory to memoize its DataOptions (see linq2db-per-connection-options-leak).");

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
		var cs = SqliteConnectionStrings.ForFile(dbPath);

		// Flag+lock serializes the first DDL per file — without the flag two threads both see
		// "not migrated" and race on the FluentMigrator journal table. File.Exists guards against
		// stale flags (file deleted without EvictAsync — e.g. race with background job drain loops +
		// test ResetAsync).
		//
		// The DataOptions memoization (see _optionsCache above) rides the SAME lock and the same
		// "first caller pays" shape: the first caller for a cacheKey ensures schema AND builds+caches
		// the DataOptions (the only call that reaches `_create`, and the only one that can allocate a
		// new linq2db interceptor); every later caller does neither — it just rewraps the cached
		// DataOptions into a fresh TContext via reflection, which is cheap enough to not be worth
		// taking back out of the lock. This also rules out two threads racing to build — and
		// separately intern — two DataOptions for the same file.
		lock (_lock)
		{
			if (!_ensured.TryGetValue(cacheKey, out _) || !File.Exists(dbPath))
			{
				_ensureSchema(cs);
				_ensured[cacheKey] = true;
			}

			if (_optionsCache.TryGetValue(cacheKey, out var cachedOptions))
				return (TContext)_optionsCtor.Invoke([new DataOptions<TContext>(cachedOptions)]);

			var fresh = _create(cs);
			_optionsCache[cacheKey] = fresh.Options;
			return fresh;
		}
	}

	public async ValueTask EvictAsync(string scopeKey, string? name = null)
	{
		var cacheKey = name is null ? scopeKey : $"{scopeKey}/{name}";
		lock (_lock)
		{
			_ensured.Remove(cacheKey);
		}
		SqliteConnection.ClearPool(new SqliteConnection(
			SqliteConnectionStrings.ForFile(ScopedDbFiles.PathFor(_baseDir, scopeKey, name))));
		await Task.CompletedTask;
	}

	public ValueTask DisposeAsync()
	{
		lock (_lock)
		{
			_ensured.Clear();
			_optionsCache.Clear();
		}
		return default;
	}
}
