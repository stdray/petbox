using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// Catalog over named task boards: metadata CRUD in PetBoxDb.TaskBoards plus the
// on-disk SQLite file lifecycle via IScopedDbFactory<TasksDb>. Mirrors LogStore.
// Explicit creation — no auto-vivify on first node write.
public interface ITaskBoardStore
{
	TasksDb GetContext(string projectKey, string board);
	Task<bool> ExistsAsync(string projectKey, string board, CancellationToken ct = default);
	// Create the board if it does not yet exist; no-op if it does. Used by the
	// upsert write path to auto-vivify on first write (deliberate exception to the
	// explicit-create rule, decided 2026-05-31 for agent ergonomics).
	Task EnsureAsync(string projectKey, string board, CancellationToken ct = default);
	Task<IReadOnlyList<TaskBoardMeta>> ListAsync(string projectKey, CancellationToken ct = default);
	// Bump UpdatedAt to now — called after a node upsert so the catalog reflects
	// last activity (the nodes live in a separate file, not this meta row).
	Task TouchAsync(string projectKey, string board, CancellationToken ct = default);
	Task<TaskBoardMeta> CreateAsync(string projectKey, string board, string? description, string kind = "free", string? specBoard = null, CancellationToken ct = default);
	// The full metadata row (Kind, SpecBoard, ClosedAt, …), or null if the board doesn't exist.
	Task<TaskBoardMeta?> FindAsync(string projectKey, string board, CancellationToken ct = default);
	// Read-modify-write the metadata row via a `with`-mutation; bumps UpdatedAt. Returns
	// false if the board doesn't exist. Use for any field change (close, spec link, …).
	Task<bool> UpdateAsync(string projectKey, string board, Func<TaskBoardMeta, TaskBoardMeta> mutate, CancellationToken ct = default);
	Task<bool> DeleteAsync(string projectKey, string board, CancellationToken ct = default);
}

public sealed partial class TaskBoardStore : ITaskBoardStore
{
	// Same name spec as logs/data dbs: starts a-z, then a-z/0-9/_/- up to 100 chars.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex NameRegex();

	readonly PetBoxDb _db;
	readonly IScopedDbFactory<TasksDb> _factory;

	public TaskBoardStore(PetBoxDb db, IScopedDbFactory<TasksDb> factory)
	{
		_db = db;
		_factory = factory;
	}

	public TasksDb GetContext(string projectKey, string board) =>
		_factory.GetDb(projectKey, board);

	public Task<bool> ExistsAsync(string projectKey, string board, CancellationToken ct = default) =>
		_db.TaskBoards.AnyAsync(b => b.ProjectKey == projectKey && b.Name == board, ct);

	public async Task<IReadOnlyList<TaskBoardMeta>> ListAsync(string projectKey, CancellationToken ct = default) =>
		await _db.TaskBoards
			.Where(b => b.ProjectKey == projectKey)
			.OrderBy(b => b.Name)
			.ToListAsync(ct);

	public Task TouchAsync(string projectKey, string board, CancellationToken ct = default) =>
		_db.TaskBoards
			.Where(b => b.ProjectKey == projectKey && b.Name == board)
			.Set(b => b.UpdatedAt, DateTime.UtcNow)
			.UpdateAsync(ct);

	public async Task EnsureAsync(string projectKey, string board, CancellationToken ct = default)
	{
		if (await ExistsAsync(projectKey, board, ct))
			return;
		await CreateAsync(projectKey, board, null, "free", ct: ct);
	}

	public Task<TaskBoardMeta?> FindAsync(string projectKey, string board, CancellationToken ct = default) =>
		_db.TaskBoards
			.Where(b => b.ProjectKey == projectKey && b.Name == board)
			.FirstOrDefaultAsync(ct)!;

	public async Task<bool> UpdateAsync(string projectKey, string board, Func<TaskBoardMeta, TaskBoardMeta> mutate, CancellationToken ct = default)
	{
		var meta = await FindAsync(projectKey, board, ct);
		if (meta is null) return false;
		await _db.UpdateAsync(mutate(meta) with { UpdatedAt = DateTime.UtcNow }, token: ct);
		return true;
	}

	public async Task<TaskBoardMeta> CreateAsync(string projectKey, string board, string? description, string kind = "free", string? specBoard = null, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(board))
			throw new ArgumentException("board name is required", nameof(board));
		if (!NameRegex().IsMatch(board))
			throw new ArgumentException("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$", nameof(board));

		var projectExists = await _db.Projects.AnyAsync(p => p.Key == projectKey, ct);
		if (!projectExists)
			throw new InvalidOperationException($"project '{projectKey}' not found");

		if (await ExistsAsync(projectKey, board, ct))
			throw new InvalidOperationException($"task board '{board}' already exists in project '{projectKey}'");

		var now = DateTime.UtcNow;
		var meta = new TaskBoardMeta
		{
			ProjectKey = projectKey,
			Name = board,
			Description = description,
			Kind = WorkflowCatalog.ParseKind(kind).ToString().ToLowerInvariant(),
			SpecBoard = string.IsNullOrWhiteSpace(specBoard) ? null : specBoard,
			CreatedAt = now,
			UpdatedAt = now,
		};
		await _db.InsertAsync(meta, token: ct);

		// Materialize file + schema eagerly (no implicit create-on-first-write).
		_factory.GetDb(projectKey, board);
		return meta;
	}

	public async Task<bool> DeleteAsync(string projectKey, string board, CancellationToken ct = default)
	{
		var deleted = await _db.TaskBoards
			.Where(b => b.ProjectKey == projectKey && b.Name == board)
			.DeleteAsync(ct);
		if (deleted == 0)
			return false;

		// Drop the cached connection before deleting the file (Windows lock).
		await _factory.EvictAsync(projectKey, board);
		ScopedDbFiles.TryDelete(ScopedDbFiles.PathFor(_factory.BaseDir, projectKey, board));
		return true;
	}
}
