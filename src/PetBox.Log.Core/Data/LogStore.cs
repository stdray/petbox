using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Log.Core.Data;

// Catalog over named logs: metadata CRUD in PetBoxDb.Logs plus the on-disk
// SQLite file lifecycle via IScopedDbFactory<LogDb>. Mirrors how DataDbsApi +
// IDataDbFactory manage user-data DBs. Request-scoped (depends on PetBoxDb).
//
// The ingestion pipeline (a singleton) does NOT use this — it resolves contexts
// straight from IScopedDbFactory<LogDb>, validating existence at the endpoint.
public interface ILogStore
{
	// Resolves the LogDb context for a named log, creating file + schema on first
	// access. Does not check metadata — callers validate existence where it matters.
	LogDb GetContext(string projectKey, string logName);

	Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default);
	Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default);
	Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default);
}

public sealed partial class LogStore : ILogStore
{
	// Same name spec as DataDbs: starts a-z, then a-z/0-9/_/- up to 100 chars.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex LogNameRegex();

	readonly PetBoxDb _db;
	readonly IScopedDbFactory<LogDb> _factory;

	public LogStore(PetBoxDb db, IScopedDbFactory<LogDb> factory)
	{
		_db = db;
		_factory = factory;
	}

	public LogDb GetContext(string projectKey, string logName) =>
		_factory.GetDb(projectKey, logName);

	public Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default) =>
		_db.Logs.AnyAsync(l => l.ProjectKey == projectKey && l.Name == logName, ct);

	public async Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default) =>
		await _db.Logs
			.Where(l => l.ProjectKey == projectKey)
			.OrderBy(l => l.Name)
			.ToListAsync(ct);

	public async Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(logName))
			throw new ArgumentException("log name is required", nameof(logName));
		if (!LogNameRegex().IsMatch(logName))
			throw new ArgumentException("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$", nameof(logName));

		var projectExists = await _db.Projects.AnyAsync(p => p.Key == projectKey, ct);
		if (!projectExists)
			throw new InvalidOperationException($"project '{projectKey}' not found");

		var exists = await ExistsAsync(projectKey, logName, ct);
		if (exists)
			throw new InvalidOperationException($"log '{logName}' already exists in project '{projectKey}'");

		var now = DateTime.UtcNow;
		var meta = new LogMeta
		{
			ProjectKey = projectKey,
			Name = logName,
			Description = description,
			CreatedAt = now,
			UpdatedAt = now,
		};
		await _db.InsertAsync(meta, token: ct);

		// Materialize the file + schema eagerly so the log is queryable and visible
		// immediately (no implicit create-on-first-write).
		_factory.GetDb(projectKey, logName);
		return meta;
	}

	public async Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default)
	{
		var deleted = await _db.Logs
			.Where(l => l.ProjectKey == projectKey && l.Name == logName)
			.DeleteAsync(ct);
		if (deleted == 0)
			return false;

		// Drop the cached connection before deleting the file (Windows lock).
		await _factory.EvictAsync(projectKey, logName);
		ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(_factory.BaseDir, projectKey, logName));
		return true;
	}
}
