using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Memory.Data;

// Catalog over named memory stores: metadata CRUD in PetBoxDb.MemoryStores plus the per-PROJECT
// SQLite file lifecycle via IScopedDbFactory<MemoryDb>. All of a project's stores share one file
// (memory/{project}.db); a store's entries are the rows whose Store column equals its name —
// exactly the tasks tier's board partition. Mirrors TaskBoardStore.
// v1 is project-scoped only. The service door auto-vivifies a store on first write
// (EnsureAsync, below — for background jobs and the reserved system stores); the agent MCP
// write path gates an unknown store to explicit creation (see MemoryTools).
public interface IMemoryStore
{
	// The project's shared memory file (holds every store's entries, partitioned by Store).
	MemoryDb GetContext(string projectKey);
	// A fresh, caller-owned, schema-ensured connection. The caller disposes it.
	// See IScopedDbFactory.NewEnsuredConnection.
	MemoryDb NewEnsuredConnection(string projectKey);
	Task<bool> ExistsAsync(string projectKey, string store, CancellationToken ct = default);
	// Create the store if it does not yet exist; no-op if it does. Used by the
	// upsert write path to auto-vivify on first write (deliberate exception to the
	// explicit-create rule, decided 2026-05-31 for agent ergonomics).
	Task EnsureAsync(string projectKey, string store, CancellationToken ct = default);
	Task<IReadOnlyList<MemoryStoreMeta>> ListAsync(string projectKey, CancellationToken ct = default);
	// Bump UpdatedAt to now — called after an entry upsert so the catalog reflects
	// last activity (entries live in a separate file, not this meta row).
	Task TouchAsync(string projectKey, string store, CancellationToken ct = default);
	Task<MemoryStoreMeta> CreateAsync(string projectKey, string store, string? description, CancellationToken ct = default);
	// Drops the catalog row AND the store's rows (entries + usage counters) from the shared
	// project file. Search docs are purged by the service door (it owns the index wiring).
	Task<bool> DeleteAsync(string projectKey, string store, CancellationToken ct = default);
}

public sealed partial class MemoryStore : IMemoryStore
{
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex NameRegex();

	// Stores that are machine plumbing rather than user knowledge — tagged IsSystem on
	// creation (incl. the auto-vivify write path, so a store is marked even though nothing
	// called CreateStoreAsync explicitly). Kept in sync with the M030/M033 backfills and
	// SessionDigestJob.Store (spec: memoverhaul store taxonomy). `autocaptured` and `canon`
	// are agent plumbing too — must not be casually deleted. IsSystem gates ONLY the system
	// badge + whole-store delete-guard, never entry writes (so canon curation via memory_upsert
	// keeps working) and — deliberately — NOT the implicit search sweep, so canon/autocaptured
	// stay in default recall. Sweep-exclusion is a separate narrow set (MemoryService).
	public static readonly IReadOnlySet<string> SystemStoreNames =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "session-digests", "autocaptured", "canon" };

	// The core-db meta lookups go through the FACTORY — one fresh, caller-owned connection per
	// method, never a request-shared one (a linq2db DataConnection is not thread-safe, and these
	// reads are driven from parallel fan-out branches of a single request).
	readonly ICoreDbFactory _core;
	readonly IScopedDbFactory<MemoryDb> _factory;

	public MemoryStore(ICoreDbFactory core, IScopedDbFactory<MemoryDb> factory)
	{
		_core = core;
		_factory = factory;
	}

	public MemoryDb GetContext(string projectKey) =>
		_factory.GetDb(projectKey);

	public MemoryDb NewEnsuredConnection(string projectKey) =>
		_factory.NewEnsuredConnection(projectKey);

	public async Task<bool> ExistsAsync(string projectKey, string store, CancellationToken ct = default)
	{
		using var db = _core.Open();
		return await db.MemoryStores.AnyAsync(s => s.ProjectKey == projectKey && s.Name == store, ct);
	}

	public async Task<IReadOnlyList<MemoryStoreMeta>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = _core.Open();
		return await db.MemoryStores
			.Where(s => s.ProjectKey == projectKey)
			.OrderBy(s => s.Name)
			.ToListAsync(ct);
	}

	public async Task TouchAsync(string projectKey, string store, CancellationToken ct = default)
	{
		using var db = _core.Open();
		await db.MemoryStores
			.Where(s => s.ProjectKey == projectKey && s.Name == store)
			.Set(s => s.UpdatedAt, DateTime.UtcNow)
			.UpdateAsync(ct);
	}

	public async Task EnsureAsync(string projectKey, string store, CancellationToken ct = default)
	{
		if (await ExistsAsync(projectKey, store, ct))
			return;
		await CreateAsync(projectKey, store, null, ct);
	}

	public async Task<MemoryStoreMeta> CreateAsync(string projectKey, string store, string? description, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(store))
			throw new ArgumentException("store name is required", nameof(store));
		if (!NameRegex().IsMatch(store))
			throw new ArgumentException("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$", nameof(store));

		using var core = _core.Open();

		var projectExists = await core.Projects.AnyAsync(p => p.Key == projectKey, ct);
		if (!projectExists)
			throw new InvalidOperationException($"project '{projectKey}' not found");

		if (await ExistsAsync(projectKey, store, ct))
			throw new InvalidOperationException($"memory store '{store}' already exists in project '{projectKey}'");

		var now = DateTime.UtcNow;
		var meta = new MemoryStoreMeta
		{
			ProjectKey = projectKey,
			Name = store,
			Description = description,
			CreatedAt = now,
			UpdatedAt = now,
			IsSystem = SystemStoreNames.Contains(store),
		};
		await core.InsertAsync(meta, token: ct);

		// Materialize the project file + schema eagerly (creating a store is now a catalog row,
		// not a file — the file may already hold other stores' rows).
		_factory.NewEnsuredConnection(projectKey).Dispose();
		return meta;
	}

	public async Task<bool> DeleteAsync(string projectKey, string store, CancellationToken ct = default)
	{
		int deleted;
		using (var core = _core.Open())
		{
			deleted = await core.MemoryStores
				.Where(s => s.ProjectKey == projectKey && s.Name == store)
				.DeleteAsync(ct);
		}
		if (deleted == 0)
			return false;

		// Stores share the project file, so delete just this store's rows (all revisions + its
		// usage counters), not the file. The search docs (search_fts/search_vec, addressed
		// Type=store) are purged by MemoryService.DeleteStoreAsync, which owns the index wiring.
		using var db = _factory.NewEnsuredConnection(projectKey);
		await db.Entries.Where(e => e.Store == store).DeleteAsync(ct);
		await db.Usage.Where(u => u.Store == store).DeleteAsync(ct);
		return true;
	}
}
