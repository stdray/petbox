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

	// Fresh caller-owned connection (caller disposes) to a named log, ensuring file +
	// schema first.
	LogDb NewEnsuredContext(string projectKey, string logName);

	Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default);
	Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default);
	Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, int? retentionDays = null, CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default);

	// Sets (or clears) the log's own retention override. `retentionDays` is the WIRE value, not
	// the stored one: 0 clears the override (the log reverts to the project/workspace/system
	// cascade), a positive value sets the window in days. Negative is refused. Returns the
	// updated row, or null if the log does not exist.
	Task<LogMeta?> UpdateRetentionDaysAsync(string projectKey, string logName, int retentionDays, CancellationToken ct = default);
}

public sealed partial class LogStore : ILogStore
{
	// Same name spec as DataDbs: starts a-z, then a-z/0-9/_/- up to 100 chars.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex LogNameRegex();

	// The core-db meta lookups go through the FACTORY — one fresh, caller-owned connection per
	// method, never a request-shared one (a linq2db DataConnection is not thread-safe).
	readonly ICoreDbFactory _core;
	readonly IScopedDbFactory<LogDb> _factory;

	public LogStore(ICoreDbFactory core, IScopedDbFactory<LogDb> factory)
	{
		_core = core;
		_factory = factory;
	}

	public LogDb GetContext(string projectKey, string logName) =>
		_factory.GetDb(projectKey, logName);

	public LogDb NewEnsuredContext(string projectKey, string logName) =>
		_factory.NewEnsuredConnection(projectKey, logName);

	public async Task<bool> ExistsAsync(string projectKey, string logName, CancellationToken ct = default)
	{
		using var db = _core.Open();
		return await db.Logs.AnyAsync(l => l.ProjectKey == projectKey && l.Name == logName, ct);
	}

	public async Task<IReadOnlyList<LogMeta>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = _core.Open();
		return await db.Logs
			.Where(l => l.ProjectKey == projectKey)
			.OrderBy(l => l.Name)
			.ToListAsync(ct);
	}

	public async Task<LogMeta> CreateAsync(string projectKey, string logName, string? description, int? retentionDays = null, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(logName))
			throw new ArgumentException("log name is required", nameof(logName));
		if (!LogNameRegex().IsMatch(logName))
			throw new ArgumentException("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$", nameof(logName));
		if (retentionDays is <= 0)
			throw new ArgumentException("retentionDays must be a positive number of days (omit it to use the project/workspace/system cascade)", nameof(retentionDays));

		using var db = _core.Open();

		var projectExists = await db.Projects.AnyAsync(p => p.Key == projectKey, ct);
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
			RetentionDays = retentionDays,
			CreatedAt = now,
			UpdatedAt = now,
		};
		await db.InsertAsync(meta, token: ct);

		// Materialize the file + schema eagerly (no implicit create-on-first-write).
		_factory.NewEnsuredConnection(projectKey, logName).Dispose();
		return meta;
	}

	public async Task<LogMeta?> UpdateRetentionDaysAsync(string projectKey, string logName, int retentionDays, CancellationToken ct = default)
	{
		if (retentionDays < 0)
			throw new ArgumentException("retentionDays must be >= 0 (0 clears the override, reverting to the project/workspace/system cascade)", nameof(retentionDays));

		using var db = _core.Open();
		var existing = await db.Logs.FirstOrDefaultAsync(l => l.ProjectKey == projectKey && l.Name == logName, ct);
		if (existing is null) return null;

		var updated = existing with
		{
			RetentionDays = retentionDays == 0 ? null : retentionDays,
			UpdatedAt = DateTime.UtcNow,
		};
		await db.UpdateAsync(updated, token: ct);
		return updated;
	}

	public async Task<bool> DeleteAsync(string projectKey, string logName, CancellationToken ct = default)
	{
		int deleted;
		using (var db = _core.Open())
		{
			deleted = await db.Logs
				.Where(l => l.ProjectKey == projectKey && l.Name == logName)
				.DeleteAsync(ct);
		}
		if (deleted == 0)
			return false;

		// Drop the cached connection before deleting the file (Windows lock).
		await _factory.EvictAsync(projectKey, logName);
		ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(_factory.BaseDir, projectKey, logName));
		return true;
	}
}
