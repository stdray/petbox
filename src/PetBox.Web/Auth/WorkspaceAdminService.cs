using LinqToDB;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Auth;

// A workspace with the two numbers every workspace admin surface shows next to it. The project count
// is of USER projects — the workspace's own memory container is not one (see IProjectDirectory).
public sealed record WorkspaceOverview(
	Workspace Workspace,
	IReadOnlyList<Project> Projects,
	int MemberCount);

// The outcome of a workspace write. Refused carries the reason; NotFound is a 404.
public abstract record WorkspaceChangeResult
{
	WorkspaceChangeResult() { }

	public sealed record Changed : WorkspaceChangeResult;
	public sealed record NotFound : WorkspaceChangeResult;
	public sealed record Refused(string Reason) : WorkspaceChangeResult;
}

// The workspace catalog: list, read, rename, delete. Everything the sysadmin workspaces table and
// the workspace-settings page do to a Workspaces row.
//
// CREATE is deliberately NOT re-implemented here — it delegates to WorkspaceProvisioning, which is
// the one place that performs the whole act (claim the creator's quota slot atomically, insert the
// row, provision the memory container). Two doors into that room is exactly what produced a
// workspace with no memory container and a workspace with no admin; this service is a third caller,
// not a third implementation.
//
// DELETE, by contrast, lives here, because it is the cascade nobody else performs — and because it
// is the writer that must not forget the memberships: they are the owner's quota ledger, so a
// workspace deleted without them turns an allowance into a one-shot ticket.
public interface IWorkspaceAdminService
{
	Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct = default);

	Task<Workspace?> GetAsync(string workspaceKey, CancellationToken ct = default);

	// The workspace plus its project list and member count in one call. `includeContainers` admits the
	// workspace's own $ws-* memory container into the project list — the workspace admin's own table
	// shows it, the counts and the delete gate never do.
	Task<WorkspaceOverview?> GetOverviewAsync(
		string workspaceKey, bool includeContainers = false, CancellationToken ct = default);

	// Delegates to WorkspaceProvisioning — see the type comment. `bypassQuota` is the sysadmin free
	// pass; a self-service caller passes false and is checked against its account's allowance INSIDE
	// the insert.
	Task<WorkspaceProvisioning.Result> CreateAsync(
		string? key,
		string? name,
		string? description,
		long? creatorUserId,
		bool bypassQuota,
		CancellationToken ct = default);

	Task<WorkspaceChangeResult> UpdateAsync(
		string workspaceKey, string? name, string? description, CancellationToken ct = default);

	// Refuses $system, and refuses a workspace that still holds USER projects (they own heavy data —
	// DBs, boards, memory, logs — so the operator must delete or move them first). The workspace's own
	// memory container is not a user project and does NOT block the delete: it is the workspace's own
	// belonging, so it dies WITH it. Cascade: container projects (full ProjectDeletion) → memberships
	// → the workspace row.
	Task<WorkspaceChangeResult> DeleteAsync(string workspaceKey, CancellationToken ct = default);
}

public sealed class WorkspaceAdminService(
	ICoreDbFactory dbf,
	IProjectDirectory projects,
	IWorkspaceMembershipService members,
	WorkspaceProvisioning provisioning) : IWorkspaceAdminService
{
	public async Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Workspaces.OrderBy(w => w.Key).ToListAsync(ct);
	}

	public async Task<Workspace?> GetAsync(string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Workspaces.FirstOrDefaultAsync(w => w.Key == workspaceKey, ct);
	}

	public async Task<WorkspaceOverview?> GetOverviewAsync(
		string workspaceKey, bool includeContainers = false, CancellationToken ct = default)
	{
		var ws = await GetAsync(workspaceKey, ct);
		if (ws is null) return null;

		var list = await projects.ListAsync(workspaceKey, includeContainers, ct);
		var memberCount = await members.CountMembersAsync(workspaceKey, ct);
		return new WorkspaceOverview(ws, list, memberCount);
	}

	public Task<WorkspaceProvisioning.Result> CreateAsync(
		string? key,
		string? name,
		string? description,
		long? creatorUserId,
		bool bypassQuota,
		CancellationToken ct = default) =>
		provisioning.CreateAsync(key, name, description, creatorUserId, bypassQuota, ct);

	public async Task<WorkspaceChangeResult> UpdateAsync(
		string workspaceKey, string? name, string? description, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			return new WorkspaceChangeResult.Refused("Name is required.");

		using var db = dbf.Open();
		var affected = await db.Workspaces
			.Where(w => w.Key == workspaceKey)
			.Set(w => w.Name, name.Trim())
			.Set(w => w.Description, description ?? string.Empty)
			.UpdateAsync(ct);

		return affected > 0 ? new WorkspaceChangeResult.Changed() : new WorkspaceChangeResult.NotFound();
	}

	public async Task<WorkspaceChangeResult> DeleteAsync(string workspaceKey, CancellationToken ct = default)
	{
		if (string.Equals(workspaceKey, WorkspaceMemory.SystemWorkspace, StringComparison.Ordinal))
			return new WorkspaceChangeResult.Refused("Cannot delete $system workspace.");

		using var db = dbf.Open();

		var all = await db.Projects
			.Where(p => p.WorkspaceKey == workspaceKey)
			.Select(p => p.Key)
			.ToListAsync(ct);

		// "Empty" means no USER projects. A workspace's own `$ws-<key>` memory container is a Projects
		// row too — provisioned with the workspace itself (WorkspaceMemory.EnsureContainerAsync, spec
		// reserved-workspace-project) — and counting it here meant a freshly created, entirely empty
		// workspace already reported "1 project(s)" and could never be deleted by anyone, sysadmin
		// included, since the container has no delete button of its own. So the gate asks about
		// projects a human made, and the container is not one.
		var userProjects = all.Where(p => !WorkspaceMemory.IsWorkspaceContainer(p)).ToList();
		if (userProjects.Count > 0)
			return new WorkspaceChangeResult.Refused(
				$"This workspace has {userProjects.Count} project(s). Delete or move them first.");

		// The workspace is empty of user projects: its container is the workspace's own belonging, so it
		// dies WITH it rather than blocking it — full cascade (ProjectDeletion), so the container's
		// memory stores/boards/keys go too and its files are reclaimed by the orphan sweepers.
		// ProjectDeletion.IsReserved would refuse this key; that guard protects a container whose
		// workspace still LIVES, which is exactly what is ending here.
		foreach (var container in all)
			await ProjectDeletion.DeleteAsync(db, container, ct);

		// Drop the memberships so no orphaned WorkspaceMember rows survive the workspace (they also ARE
		// the owner's quota ledger — leaving them would make an allowance a one-shot ticket), then
		// delete the workspace itself. The memberships go through their own service: it is the single
		// door every membership writer uses, and the one a cache would invalidate from. It opens its
		// own connection — safe here precisely because no transaction is held (core.db runs
		// Cache=Shared; a nested core-db call under an open transaction raises an un-retried
		// SQLITE_LOCKED).
		await members.RemoveWorkspaceAsync(workspaceKey, ct);

		var affected = await db.Workspaces.Where(w => w.Key == workspaceKey).DeleteAsync(ct);
		return affected > 0 ? new WorkspaceChangeResult.Changed() : new WorkspaceChangeResult.NotFound();
	}
}
