using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Auth;

// The outcome of a project write. Refused carries the reason (a refusal nobody can see is a silent
// failure); NotFound is the answer to a project that is not there — the caller 404s rather than
// explaining. Same shape as KeyUpdateResult, for the same reasons.
public abstract record ProjectChangeResult
{
	ProjectChangeResult() { }

	public sealed record Created(Project Project) : ProjectChangeResult;
	public sealed record Deleted : ProjectChangeResult;
	public sealed record NotFound : ProjectChangeResult;
	public sealed record Refused(string Reason) : ProjectChangeResult;
}

// THE catalog of projects — every read and every write of the Projects table that the web layer
// performs goes through here.
//
// It started as the one place that answers "does this project live in this workspace?" — the
// question behind the whole {workspaceKey}/{projectKey} IDOR class (workspace-access-isolation) —
// because ProjectWorkspaceBindingFilter is a FILTER, i.e. pipeline code, and the DB is visible only
// in the service layer. It is now the whole directory, for the second reason in AGENTS.md: a GET of
// a project page opens core.db 7-9 times and NOTHING can be memoised while the readers are scattered
// across pages. This is the one implementation a cache would go into, with no caller reaching around
// it. (No cache today — deliberately. Nothing here assumes one.)
//
// Workspace memory containers ("$workspace" / "$ws-*") are Projects rows but are NOT user projects:
// they have no logs, dbs or boards and never belong in a project tree or a project count. Every list
// here therefore filters them out BY DEFAULT, and a caller that genuinely wants them (the workspace
// admin's own project table, the delete cascade) must say so — the default is the safe one, because
// the count that included the container is what made a freshly created workspace undeletable.
public interface IProjectDirectory
{
	// False for BOTH "no such project" and "project of another workspace" — the caller 404s either
	// way, so the route cannot be used to probe for the existence of another tenant's project.
	Task<bool> BelongsAsync(string projectKey, string workspaceKey, CancellationToken ct = default);

	Task<bool> ExistsAsync(string projectKey, CancellationToken ct = default);

	// By key alone. Project keys are globally unique, so this is well-defined — but a page that has a
	// route WORKSPACE must use GetInWorkspaceAsync instead: proving the project exists is not proving
	// the caller is entitled to it.
	Task<Project?> GetAsync(string projectKey, CancellationToken ct = default);

	// The ownership predicate is welded into the statement: null means "not there OR not yours", and
	// the two are indistinguishable to the caller by design.
	Task<Project?> GetInWorkspaceAsync(string workspaceKey, string projectKey, CancellationToken ct = default);

	// The user projects of a workspace, ordered by key. `includeContainers` admits the workspace's own
	// $ws-* memory container — only the workspace admin's project table and the delete cascade want it.
	Task<IReadOnlyList<Project>> ListAsync(
		string workspaceKey, bool includeContainers = false, CancellationToken ct = default);

	// EVERY project, across every workspace, ordered by key — the fleet-wide view the provisioning
	// surface (project_list with no workspace) asks for. It is NOT a page's question: a page always has
	// a workspace, and asking this one there would be the IDOR. `includeContainers` admits the $ws-*
	// memory containers, which the admin/provisioning surfaces do see.
	Task<IReadOnlyList<Project>> ListAllAsync(bool includeContainers = false, CancellationToken ct = default);

	// The user projects of SEVERAL workspaces at once, grouped by workspace key — the sidebar's whole
	// project tree in ONE read. NavigationContext used to open core.db 3-4 times per request to build
	// this; that is the measured cost this method exists to collapse.
	Task<IReadOnlyDictionary<string, IReadOnlyList<Project>>> ListByWorkspaceAsync(
		IReadOnlyCollection<string> workspaceKeys, CancellationToken ct = default);

	Task<int> CountAsync(string workspaceKey, bool includeContainers = false, CancellationToken ct = default);

	// Create a project IN a workspace. The workspace comes from the caller's ROUTE, never from a form
	// field (authz-bypass-project-create), and the key rules — reserved URL segments, no projects in
	// $system, no duplicate, the workspace must exist — are enforced HERE so that a second create page
	// (or the project_create MCP tool) cannot forget one of them. `sandbox` flags the project as the
	// write-gate containment target for sandbox-only API keys (only the provisioning surface sets it).
	Task<ProjectChangeResult> CreateAsync(
		string workspaceKey, string? key, string? name, string? description,
		bool sandbox = false, CancellationToken ct = default);

	// Delete a project and everything it owns in core.db (keys, health endpoints, data/log/board/memory
	// metadata, relations, settings) — see ProjectDeletion for the cascade and the file-scope boundary.
	// Refused for the reserved built-ins. The workspace is part of the ADDRESS, not a filter applied
	// afterwards: a forged POST naming another tenant's project matches nothing.
	Task<ProjectChangeResult> DeleteAsync(string workspaceKey, string projectKey, CancellationToken ct = default);
}

public sealed class ProjectDirectory(ICoreDbFactory dbf) : IProjectDirectory
{
	// Project keys that would collide with a URL segment of the /ui tree.
	static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		"logs", "traces", "config", "admin", "projects", "sys", "tasks", "data", "settings",
	};

	public async Task<bool> BelongsAsync(string projectKey, string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Projects.AnyAsync(
			p => p.Key == projectKey && p.WorkspaceKey == workspaceKey, ct);
	}

	public async Task<bool> ExistsAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Projects.AnyAsync(p => p.Key == projectKey, ct);
	}

	public async Task<Project?> GetAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Projects.FirstOrDefaultAsync(p => p.Key == projectKey, ct);
	}

	public async Task<Project?> GetInWorkspaceAsync(
		string workspaceKey, string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Projects.FirstOrDefaultAsync(
			p => p.Key == projectKey && p.WorkspaceKey == workspaceKey, ct);
	}

	public async Task<IReadOnlyList<Project>> ListAsync(
		string workspaceKey, bool includeContainers = false, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		var rows = await db.Projects
			.Where(p => p.WorkspaceKey == workspaceKey)
			.OrderBy(p => p.Key)
			.ToListAsync(ct);

		// Filtered in memory, not in SQL: IsWorkspaceContainer is the ONE definition of what a
		// container key is (WorkspaceMemory), and re-expressing it as a translatable predicate would
		// be a second one — free to drift from the first.
		return includeContainers
			? rows
			: [.. rows.Where(p => !WorkspaceMemory.IsWorkspaceContainer(p.Key))];
	}

	public async Task<IReadOnlyList<Project>> ListAllAsync(
		bool includeContainers = false, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		var rows = await db.Projects.OrderBy(p => p.Key).ToListAsync(ct);
		return includeContainers
			? rows
			: [.. rows.Where(p => !WorkspaceMemory.IsWorkspaceContainer(p.Key))];
	}

	public async Task<IReadOnlyDictionary<string, IReadOnlyList<Project>>> ListByWorkspaceAsync(
		IReadOnlyCollection<string> workspaceKeys, CancellationToken ct = default)
	{
		if (workspaceKeys.Count == 0)
			return new Dictionary<string, IReadOnlyList<Project>>(StringComparer.Ordinal);

		var keys = workspaceKeys.ToHashSet(StringComparer.Ordinal);

		using var db = dbf.Open();
		var rows = await db.Projects
			.Where(p => keys.Contains(p.WorkspaceKey))
			.OrderBy(p => p.Key)
			.ToListAsync(ct);

		return rows
			.Where(p => !WorkspaceMemory.IsWorkspaceContainer(p.Key))
			.GroupBy(p => p.WorkspaceKey, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => (IReadOnlyList<Project>)[.. g], StringComparer.Ordinal);
	}

	public async Task<int> CountAsync(
		string workspaceKey, bool includeContainers = false, CancellationToken ct = default)
	{
		if (includeContainers)
		{
			using var db = dbf.Open();
			return await db.Projects.CountAsync(p => p.WorkspaceKey == workspaceKey, ct);
		}

		return (await ListAsync(workspaceKey, includeContainers: false, ct)).Count;
	}

	public async Task<ProjectChangeResult> CreateAsync(
		string workspaceKey, string? key, string? name, string? description,
		bool sandbox = false, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
			return new ProjectChangeResult.Refused("Key and Name are required.");

		if (string.Equals(workspaceKey, WorkspaceMemory.SystemWorkspace, StringComparison.Ordinal))
			return new ProjectChangeResult.Refused(
				"Cannot create projects in $system. It hosts PetBox-internal services only.");

		if (ReservedKeys.Contains(key))
			return new ProjectChangeResult.Refused(
				$"Project key '{key}' is reserved (collides with a URL segment).");

		// A '$'-prefixed key would collide with the reserved workspace memory containers, which are
		// never user projects (spec reserved-workspace-project) — and no page had this check.
		if (WorkspaceMemory.IsWorkspaceContainer(key) || key.StartsWith('$'))
			return new ProjectChangeResult.Refused(
				$"Project key '{key}' is reserved ('$' names a built-in container).");

		using var db = dbf.Open();

		// The workspace must EXIST. A page's workspace comes from its route (it exists by the time the
		// filter let the request through), but the provisioning surface takes it as an argument — and a
		// project row pointing at no workspace is an orphan nothing can reach.
		if (!await db.Workspaces.AnyAsync((Workspace w) => w.Key == workspaceKey, ct))
			return new ProjectChangeResult.NotFound();

		// Keys are GLOBALLY unique, so the duplicate check is not workspace-scoped — a key taken in
		// another workspace is taken.
		if (await db.Projects.AnyAsync(p => p.Key == key, ct))
			return new ProjectChangeResult.Refused($"Project '{key}' already exists.");

		var project = new Project
		{
			Key = key,
			WorkspaceKey = workspaceKey,
			Name = name,
			Description = description ?? string.Empty,
			Sandbox = sandbox,
		};
		await db.InsertAsync(project, token: ct);
		return new ProjectChangeResult.Created(project);
	}

	public async Task<ProjectChangeResult> DeleteAsync(
		string workspaceKey, string projectKey, CancellationToken ct = default)
	{
		if (ProjectDeletion.IsReserved(projectKey))
			return new ProjectChangeResult.Refused($"Cannot delete the reserved project '{projectKey}'.");

		using var db = dbf.Open();

		// The workspace is proven BEFORE the cascade, and it is what makes this address safe: a POST
		// from another workspace's admin naming this project finds nothing to delete. (ProjectDeletion
		// itself is keyed by project alone — it is the cascade, not the guard.)
		if (!await db.Projects.AnyAsync(p => p.Key == projectKey && p.WorkspaceKey == workspaceKey, ct))
			return new ProjectChangeResult.NotFound();

		var deleted = await ProjectDeletion.DeleteAsync(db, projectKey, ct);
		return deleted ? new ProjectChangeResult.Deleted() : new ProjectChangeResult.NotFound();
	}
}
