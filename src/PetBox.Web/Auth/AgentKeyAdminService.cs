using LinqToDB;
using PetBox.Core.Data;

namespace PetBox.Web.Auth;

// One row of the agent-keys admin views. `Key` is the raw key value — it is already stored in
// clear in ApiKeys and both views are admin-gated; it is what the revoke form posts back.
public sealed record AgentKeyRow(
	string Key,
	string Name,
	string ProjectKey,
	string Scopes,
	DateTime CreatedAt,
	DateTime? ExpiresAt,
	bool Expired);

// The list+revoke logic behind BOTH agent-key admin pages: the system-wide one
// (/ui/admin/sys/agent-keys, SysAdmin, every key) and the workspace-scoped one
// (/ui/admin/ws/{workspaceKey}/agent-keys, WorkspaceAdmin, only the keys of THIS workspace's
// projects). `workspaceKey == null` IS the system scope — sysadmin sees and revokes everything;
// a non-null one confines both operations to the projects of that workspace.
//
// The scoping lives HERE, not in the page, because revoke is addressed by the key VALUE: a page
// that merely filtered its rendered list would still delete any key a forged POST named
// (workspace-access-isolation's IDOR, one layer down). RevokeAsync therefore re-proves ownership
// inside the DELETE itself — the key is deleted only if it belongs to a project of the given
// workspace, in ONE statement, so there is no TOCTOU window between the check and the delete.
//
// A cross-project key (ProjectKey == "*") belongs to no single workspace, so it never appears in
// a workspace-scoped list and can never be revoked from one: only a sysadmin may kill it.
public sealed class AgentKeyAdminService(ICoreDbFactory dbf)
{
	public async Task<IReadOnlyList<AgentKeyRow>> ListAsync(string? workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		// All DB-minted keys (expiring and permanent). Config-declared keys (appsettings/env) are
		// not rows and don't appear here.
		var q = db.ApiKeys.AsQueryable();
		if (workspaceKey is not null)
		{
			q = q.Where(k => db.Projects.Any(p => p.Key == k.ProjectKey && p.WorkspaceKey == workspaceKey));
		}

		var now = DateTime.UtcNow;
		var rows = await q.OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
		return [.. rows.Select(k => new AgentKeyRow(
			k.Key, k.Name, k.ProjectKey, k.Scopes, k.CreatedAt, k.ExpiresAt,
			k.ExpiresAt != null && k.ExpiresAt <= now))];
	}

	// Returns false when the key does not exist OR does not belong to `workspaceKey` — the caller
	// answers 404 either way, so a workspace admin cannot even probe for the existence of another
	// tenant's key.
	public async Task<bool> RevokeAsync(string key, string? workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var q = db.ApiKeys.Where(k => k.Key == key);
		if (workspaceKey is not null)
		{
			q = q.Where(k => db.Projects.Any(p => p.Key == k.ProjectKey && p.WorkspaceKey == workspaceKey));
		}

		return await q.DeleteAsync(ct) > 0;
	}
}
