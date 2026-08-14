using System.Collections.Concurrent;
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
	//
	// ConcurrentDictionary, not Dictionary+_lock: this is read on the HOT path (see
	// NewEnsuredConnection below), and it must be readable WITHOUT taking _lock. Production is a
	// single vCPU with Kestrel already logging thread-pool-starvation heartbeats, and connections
	// open here at ~16/sec from five competing background jobs plus request paths — serializing
	// every connection open on one lock (which an earlier version of this fix did, reasoning that
	// the reflective rebuild was "cheap enough") would trade the OOM for exactly the kind of
	// contention that host cannot absorb. Only the FIRST build for a (scopeKey, name) — the one
	// call that can allocate a new linq2db interceptor — takes _lock; see below.
	readonly ConcurrentDictionary<string, DataOptions> _optionsCache = new();

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

		// LOCK-FREE FAST PATH — this is the hot one and it is deliberate; do not move this back
		// under _lock. Once a (scopeKey, name)'s DataOptions has been built (see the slow path
		// below), every later caller reaches this branch: it takes no lock, runs no schema check,
		// and never calls `_create` — it only reads the ConcurrentDictionary and reflectively
		// rewraps the cached DataOptions into a fresh TContext. Production opens connections here
		// at ~16/sec from five competing background jobs plus request paths on a single vCPU that
		// already logs Kestrel thread-pool-starvation heartbeats; serializing that on one lock is
		// not an option regardless of how cheap the rebuild is per call.
		// File.Exists is still checked on every call (a plain stat(), not a lock) to preserve the
		// original guard: a file deleted without EvictAsync (background job drain loops, test
		// ResetAsync) must fall through to the slow path and re-run schema, not silently hand out a
		// connection to a file that no longer exists.
		if (File.Exists(dbPath) && _optionsCache.TryGetValue(cacheKey, out var cachedOptions))
			return (TContext)_optionsCtor.Invoke([new DataOptions<TContext>(cachedOptions)]);

		// SLOW PATH — only the first caller for a (scopeKey, name) (or a caller after EvictAsync /
		// an out-of-band file deletion) reaches here. Flag+lock serializes the first DDL per file —
		// without the flag two threads both see "not migrated" and race on the FluentMigrator
		// journal table — and the DataOptions build rides the SAME lock, so the one call that can
		// allocate a new linq2db interceptor (`_create`, which runs XDb.CreateOptions) never races
		// itself. Double-checked: another thread may have already built+cached the DataOptions (and
		// possibly re-ensured schema) while this one was waiting for the lock.
		lock (_lock)
		{
			if (!_ensured.TryGetValue(cacheKey, out _) || !File.Exists(dbPath))
			{
				_ensureSchema(cs);
				_ensured[cacheKey] = true;
			}

			if (_optionsCache.TryGetValue(cacheKey, out cachedOptions))
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
