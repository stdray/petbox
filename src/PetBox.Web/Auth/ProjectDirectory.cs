using LinqToDB;
using PetBox.Core.Data;

namespace PetBox.Web.Auth;

// The one place that answers "does this project live in this workspace?" — the question behind the
// whole {workspaceKey}/{projectKey} IDOR class (workspace-access-isolation).
//
// It exists so that ProjectWorkspaceBindingFilter — a FILTER, i.e. pipeline code — does not open
// core.db itself: the DB is visible only in the service layer, and everything above it asks a
// service. That boundary is also where a cache would go if the per-request read ever shows up in a
// profile: one implementation to memoize, with no caller reaching around it.
public interface IProjectDirectory
{
	Task<bool> BelongsAsync(string projectKey, string workspaceKey, CancellationToken ct = default);
}

public sealed class ProjectDirectory(ICoreDbFactory dbf) : IProjectDirectory
{
	// False for BOTH "no such project" and "project of another workspace" — the caller 404s either
	// way, so the route cannot be used to probe for the existence of another tenant's project.
	public async Task<bool> BelongsAsync(string projectKey, string workspaceKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.Projects.AnyAsync(
			p => p.Key == projectKey && p.WorkspaceKey == workspaceKey, ct);
	}
}
