using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Memory.Data;

// Catalog over named memory stores: metadata CRUD in PetBoxDb.MemoryStores plus
// the on-disk file lifecycle via IScopedDbFactory<MemoryDb>. Mirrors LogStore.
// v1 is project-scoped only. Explicit creation — no auto-vivify.
public interface IMemoryStore
{
	MemoryDb GetContext(string projectKey, string store);
	Task<bool> ExistsAsync(string projectKey, string store, CancellationToken ct = default);
	// Create the store if it does not yet exist; no-op if it does. Used by the
	// upsert write path to auto-vivify on first write (deliberate exception to the
	// explicit-create rule, decided 2026-05-31 for agent ergonomics).
	Task EnsureAsync(string projectKey, string store, CancellationToken ct = default);
	Task<IReadOnlyList<MemoryStoreMeta>> ListAsync(string projectKey, CancellationToken ct = default);
	Task<MemoryStoreMeta> CreateAsync(string projectKey, string store, string? description, CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string store, CancellationToken ct = default);
}

public sealed partial class MemoryStore : IMemoryStore
{
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex NameRegex();

	readonly PetBoxDb _db;
	readonly IScopedDbFactory<MemoryDb> _factory;

	public MemoryStore(PetBoxDb db, IScopedDbFactory<MemoryDb> factory)
	{
		_db = db;
		_factory = factory;
	}

	public MemoryDb GetContext(string projectKey, string store) =>
		_factory.GetDb(projectKey, store);

	public Task<bool> ExistsAsync(string projectKey, string store, CancellationToken ct = default) =>
		_db.MemoryStores.AnyAsync(s => s.ProjectKey == projectKey && s.Name == store, ct);

	public async Task<IReadOnlyList<MemoryStoreMeta>> ListAsync(string projectKey, CancellationToken ct = default) =>
		await _db.MemoryStores
			.Where(s => s.ProjectKey == projectKey)
			.OrderBy(s => s.Name)
			.ToListAsync(ct);

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

		var projectExists = await _db.Projects.AnyAsync(p => p.Key == projectKey, ct);
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
		};
		await _db.InsertAsync(meta, token: ct);

		_factory.GetDb(projectKey, store);
		return meta;
	}

	public async Task<bool> DeleteAsync(string projectKey, string store, CancellationToken ct = default)
	{
		var deleted = await _db.MemoryStores
			.Where(s => s.ProjectKey == projectKey && s.Name == store)
			.DeleteAsync(ct);
		if (deleted == 0)
			return false;

		await _factory.EvictAsync(projectKey, store);
		ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(_factory.BaseDir, projectKey, store));
		return true;
	}
}
