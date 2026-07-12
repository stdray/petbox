using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Tasks.Workflow;

namespace PetBox.Tasks.Data;

// Catalog over named task boards: metadata CRUD in PetBoxDb.TaskBoards plus the
// per-PROJECT SQLite file lifecycle via IScopedDbFactory<TasksDb>. All of a project's
// boards share one file (tasks/<project>.db); a board's nodes are the rows whose Board
// column equals its name. Explicit creation — no auto-vivify on first node write.
public interface ITaskBoardStore
{
	// The project's shared plan file (holds every board's nodes, partitioned by Board).
	TasksDb GetContext(string projectKey);
	// A fresh, caller-owned, schema-ensured connection. The caller disposes it.
	// See IScopedDbFactory.NewEnsuredConnection.
	TasksDb NewEnsuredConnection(string projectKey);
	Task<bool> ExistsAsync(string projectKey, string board, CancellationToken ct = default);
	// Create the board if it does not yet exist; no-op if it does. Used by the
	// upsert write path to auto-vivify on first write (deliberate exception to the
	// explicit-create rule, decided 2026-05-31 for agent ergonomics).
	Task EnsureAsync(string projectKey, string board, CancellationToken ct = default);
	Task<IReadOnlyList<TaskBoardMeta>> ListAsync(string projectKey, CancellationToken ct = default);
	// Bump UpdatedAt to now — called after a node upsert so the catalog reflects
	// last activity (the nodes live in a separate file, not this meta row).
	Task TouchAsync(string projectKey, string board, CancellationToken ct = default);
	Task<TaskBoardMeta> CreateAsync(string projectKey, string board, string? description, string kind = "simple", string? specBoard = null, string? methodologyInstance = null, CancellationToken ct = default);
	// The full metadata row (Kind, SpecBoard, ClosedAt, …), or null if the board doesn't exist.
	Task<TaskBoardMeta?> FindAsync(string projectKey, string board, CancellationToken ct = default);
	// The board owning a node's active revision (ActiveTo == null), or null if no active
	// row carries this NodeId. Lets callers resolve a node from its stable id alone, without
	// knowing which board it lives on (boards share one plan file, partitioned by Board).
	Task<string?> FindBoardByNodeIdAsync(string projectKey, string nodeId, CancellationToken ct = default);
	// The stable NodeId of the active node addressed by (board, slug) — Key is unique within a
	// board, so this resolves the human-readable slug-URL to a node. null if no active node on
	// that board carries the slug.
	Task<string?> FindNodeIdBySlugAsync(string projectKey, string board, string slug, CancellationToken ct = default);
	// The workspace owning a project (Projects.WorkspaceKey), or null if the project is
	// unknown — used to build per-node UI permalinks.
	Task<string?> FindProjectWorkspaceAsync(string projectKey, CancellationToken ct = default);
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
	readonly DataOptions<PetBoxDb> _coreOptions;
	readonly IScopedDbFactory<TasksDb> _factory;

	public TaskBoardStore(PetBoxDb db, IScopedDbFactory<TasksDb> factory)
	{
		_db = db;
		// The injected PetBoxDb is AddScoped (Program.cs) — ONE LinqToDB DataConnection per HTTP
		// request — and a DataConnection is NOT thread-safe. Read paths here are fanned out in
		// parallel within a single request scope (CrossScopeTaskSearchService runs up to
		// MaxProjectConcurrency per-project searches at once, each reaching FindAsync/ListAsync),
		// and the overlapping calls trampled the shared SqliteCommand's parameter list — prod 500'd
		// with "Must add values for the following parameters: @projectKey, @board" (and friends:
		// "Collection was modified", ObjectDisposedException — one race, several faces).
		// So the READS take a fresh, caller-owned connection per call (same pattern the tasks-file
		// side already uses: IScopedDbFactory.NewEnsuredConnection). Cloning the scoped connection's
		// DataOptions keeps the provider, the connection string and the SHARED mapping schema — no
		// per-connection MappingSchema is built (that was the prod OOM, see PetBoxDb.SharedMappingSchema),
		// and Microsoft.Data.Sqlite pools the underlying connection.
		_coreOptions = new DataOptions<PetBoxDb>(db.Options);
		_factory = factory;
	}

	// A fresh core-db connection, owned (and disposed) by the calling method. Never share it.
	PetBoxDb NewCoreConnection() => new(_coreOptions);

	public TasksDb GetContext(string projectKey) =>
		_factory.GetDb(projectKey);

	public TasksDb NewEnsuredConnection(string projectKey) =>
		_factory.NewEnsuredConnection(projectKey);

	public async Task<bool> ExistsAsync(string projectKey, string board, CancellationToken ct = default)
	{
		using var db = NewCoreConnection();
		return await db.TaskBoards.AnyAsync(b => b.ProjectKey == projectKey && b.Name == board, ct);
	}

	public async Task<IReadOnlyList<TaskBoardMeta>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = NewCoreConnection();
		return await db.TaskBoards
			.Where(b => b.ProjectKey == projectKey)
			.OrderBy(b => b.Name)
			.ToListAsync(ct);
	}

	public async Task<string?> FindProjectWorkspaceAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = NewCoreConnection();
		return await db.Projects.Where(p => p.Key == projectKey).Select(p => p.WorkspaceKey).FirstOrDefaultAsync(ct);
	}

	public Task TouchAsync(string projectKey, string board, CancellationToken ct = default) =>
		_db.TaskBoards
			.Where(b => b.ProjectKey == projectKey && b.Name == board)
			.Set(b => b.UpdatedAt, DateTime.UtcNow)
			.UpdateAsync(ct);

	public async Task EnsureAsync(string projectKey, string board, CancellationToken ct = default)
	{
		if (await ExistsAsync(projectKey, board, ct))
			return;
		await CreateAsync(projectKey, board, null, "simple", ct: ct);
	}

	public async Task<TaskBoardMeta?> FindAsync(string projectKey, string board, CancellationToken ct = default)
	{
		using var db = NewCoreConnection();
		return await db.TaskBoards
			.Where(b => b.ProjectKey == projectKey && b.Name == board)
			.FirstOrDefaultAsync(ct)!;
	}

	public async Task<string?> FindBoardByNodeIdAsync(string projectKey, string nodeId, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		return await ctx.PlanNodes
			.Where(n => n.NodeId == nodeId && n.ActiveTo == null)
			.Select(n => n.Board)
			.FirstOrDefaultAsync(ct)!;
	}

	public async Task<string?> FindNodeIdBySlugAsync(string projectKey, string board, string slug, CancellationToken ct = default)
	{
		using var ctx = _factory.NewEnsuredConnection(projectKey);
		return await ctx.PlanNodes
			.Where(n => n.Board == board && n.Key == slug && n.ActiveTo == null)
			.Select(n => n.NodeId)
			.FirstOrDefaultAsync(ct)!;
	}

	public async Task<bool> UpdateAsync(string projectKey, string board, Func<TaskBoardMeta, TaskBoardMeta> mutate, CancellationToken ct = default)
	{
		var meta = await FindAsync(projectKey, board, ct);
		if (meta is null) return false;
		await _db.UpdateAsync(mutate(meta) with { UpdatedAt = DateTime.UtcNow }, token: ct);
		return true;
	}

	public async Task<TaskBoardMeta> CreateAsync(string projectKey, string board, string? description, string kind = "simple", string? specBoard = null, string? methodologyInstance = null, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(board))
			throw new ArgumentException("board name is required", nameof(board));
		if (!NameRegex().IsMatch(board))
			throw new ArgumentException("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$", nameof(board));
		// `node` is reserved: the node-by-id route /tasks/node/{nodeId} would collide with the
		// slug route /tasks/{board}/{slug} if a board were named "node" (node-slug-addressable).
		if (board == "node")
			throw new ArgumentException("board name 'node' is reserved (collides with the /tasks/node/{id} route)", nameof(board));

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
			// Builtin kinds normalize to their canonical enum name; anything else is stored
			// VERBATIM (lowercased) — a definition-declared kind. Validation (unknown-kind
			// rejection) lives in TasksService.CreateBoardAsync, the single door: the store
			// is core-db land and must not async-reach into the tasks db for the definition.
			Kind = Enum.TryParse<BoardKind>(kind, ignoreCase: true, out var bk)
				? bk.ToString().ToLowerInvariant()
				: kind.Trim().ToLowerInvariant(),
			SpecBoard = string.IsNullOrWhiteSpace(specBoard) ? null : specBoard,
			MethodologyInstance = string.IsNullOrWhiteSpace(methodologyInstance) ? null : methodologyInstance.Trim().ToLowerInvariant(),
			CreatedAt = now,
			UpdatedAt = now,
		};
		await _db.InsertAsync(meta, token: ct);

		// Materialize the project file + schema eagerly (no implicit create-on-first-write).
		_factory.NewEnsuredConnection(projectKey).Dispose();
		return meta;
	}

	public async Task<bool> DeleteAsync(string projectKey, string board, CancellationToken ct = default)
	{
		var deleted = await _db.TaskBoards
			.Where(b => b.ProjectKey == projectKey && b.Name == board)
			.DeleteAsync(ct);
		if (deleted == 0)
			return false;

		// Boards share the project file, so delete just this board's rows (all revisions),
		// not the file. Relations (in petbox.db) bind to NodeId and are left as-is — they
		// resolve to "missing", same as when a board file used to be dropped.
		using var db = _factory.NewEnsuredConnection(projectKey);
		await db.PlanNodes.Where(n => n.Board == board).DeleteAsync(ct);
		return true;
	}
}
