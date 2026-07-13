using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Models;

namespace PetBox.Core.Data;

// The COUNTS door onto core.db. Every table these numbers come from already has a service that owns
// it (ProjectDirectory / AgentKeyAdminService / IDataDbCatalog / IHealthReportService / the Settings
// store) — none of them COUNTS, which is why the three landing pages (Admin/Index, Dashboard/Index,
// ProjectHome/Index) still opened core.db directly for a handful of "how many" numbers. This is that
// missing door.
//
// ONE METHOD PER PAGE, ONE CONNECTION PER CALL. A rollup that turned around and called five existing
// services in a row would satisfy DbLayerGuardTests (no page holds a factory) while making the actual
// problem — a GET opening core.db ~10 times / running ~13-14 statements — worse, not better: five
// services means five separate connections instead of one. So this type talks to the tables directly
// (same shape as ProjectCatalog / ProjectDeletion next door) and every count is a SQL COUNT/GROUP BY,
// never a materialize-then-.Count() in memory.
//
// THIS IS ALSO THE CACHE ATTACHMENT POINT (work `db-cache-behind-services`). core.db has no cache
// anywhere today because its readers were scattered across pages — there was no single place to put
// one. There is now: a cache would wrap GetAdminRollupAsync / GetWorkspaceRollupAsync /
// GetProjectRollupAsync (keyed by nothing / workspace key / project key respectively) and invalidate
// on the writes that touch these tables. Nothing here assumes one — this only makes one possible.
//
// ACCESS SCOPING IS THE CALLER'S JOB, not this service's. Every method below counts exactly the rows
// its caller asks for (a project-key set, or nothing = the whole fleet for the sysadmin page) — it
// does not re-derive "which projects may this caller see". That answer already lives in
// IProjectDirectory / ProjectWorkspaceBindingFilter / the WorkspaceViewer policy, upstream of every
// call here. A rollup that ALSO tried to own scoping would be a second copy of that predicate, which
// is exactly the drift AGENTS.md's "ten copies of the ownership check" warns about.
public interface ICoreDbRollupService
{
	// /ui/admin/sys — sysadmin landing. NOT workspace-scoped: sysadmin sees the whole fleet, and
	// ProjectCount deliberately counts every Projects row (including the $ws-* memory containers),
	// matching the page's pre-existing `db.Projects.CountAsync()` with no container filter.
	Task<AdminRollup> GetAdminRollupAsync(CancellationToken ct = default);

	// Dashboard/Index — the fleet rollup for one workspace's projects. `projectKeys` is the caller's
	// ALREADY-RESOLVED project set (IProjectDirectory.ListAsync(workspaceKey) — containers excluded,
	// same as before). Per-project Log/Db/Key counts are scoped to exactly that set via a SQL
	// GROUP BY. The health-report read is NOT SQL-scoped by project — same as the code this replaces:
	// a report's project lives inside its Tags string, which is not a queryable column, so every
	// caller reads the latest-per-(Svc, parsed-project-tag) report across ALL projects and filters
	// in memory against its own projectKeys before rendering. That in-memory filter is what actually
	// enforces the workspace boundary; the caller must keep applying it (ProjectHome/Dashboard
	// already do, unchanged).
	Task<WorkspaceRollup> GetWorkspaceRollupAsync(
		IReadOnlyCollection<string> projectKeys, CancellationToken ct = default);

	// ProjectHome/Index — one project's counts + its own latest health, grouped by (Svc, raw Tags)
	// exactly as the page did inline (a DIFFERENT grouping key from the workspace rollup above — see
	// the class comment on WorkspaceRollup vs. this method for why they are not unified).
	Task<ProjectRollup> GetProjectRollupAsync(string projectKey, CancellationToken ct = default);
}

public sealed record AdminRollup(
	int WorkspaceCount, int ProjectCount, int UserCount, int SettingOverrideCount, int AgentKeyCount);

public sealed record WorkspaceRollup(
	IReadOnlyDictionary<string, int> LogCount,
	IReadOnlyDictionary<string, int> DbCount,
	IReadOnlyDictionary<string, int> KeyCount,
	// Latest HealthReport per (Svc, tag "project") — UNFILTERED by workspace (see the interface
	// doc). The caller filters by its own projectKeys before it renders anything.
	IReadOnlyList<HealthReport> LatestHealthReports);

public sealed record ProjectRollup(
	int LogCount, int DbCount, int KeyCount,
	// Latest HealthReport per (Svc, raw Tags) — this project's own reports only (Tags is filtered in
	// memory by the caller against ProjectKey, same as the page did inline).
	IReadOnlyList<HealthReport> LatestHealth);

public sealed class CoreDbRollupService(ICoreDbFactory dbf) : ICoreDbRollupService
{
	public async Task<AdminRollup> GetAdminRollupAsync(CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var workspaceCount = await db.Workspaces.CountAsync(ct);
		var projectCount = await db.Projects.CountAsync(ct);
		var userCount = await db.Users.CountAsync(ct);
		// System-wide setting rows (defaults) only — per-project/per-user overrides are a different
		// page's question, same scope the page applied before.
		var settingOverrideCount = await db.Settings.CountAsync(s => s.Scope == "System", ct);
		// All DB-minted API keys — the Agent keys overview counts the same rows it lists.
		var agentKeyCount = await db.ApiKeys.CountAsync(ct);

		return new AdminRollup(workspaceCount, projectCount, userCount, settingOverrideCount, agentKeyCount);
	}

	public async Task<WorkspaceRollup> GetWorkspaceRollupAsync(
		IReadOnlyCollection<string> projectKeys, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var keys = projectKeys.ToHashSet(StringComparer.Ordinal);

		var logCount = await CountByProjectAsync(db.Logs.Where(l => keys.Contains(l.ProjectKey)).Select(l => l.ProjectKey), ct);
		var dbCount = await CountByProjectAsync(db.DataDbs.Where(d => keys.Contains(d.ProjectKey)).Select(d => d.ProjectKey), ct);
		var keyCount = await CountByProjectAsync(db.ApiKeys.Where(k => keys.Contains(k.ProjectKey)).Select(k => k.ProjectKey), ct);

		// Latest report per (Svc, project tag). Identity is (Svc, project) — the rest of the tags
		// (host, elapsedMs, reason, …) are volatile payload of a single report, so grouping by the raw
		// Tags string would resurface the whole history. The project tag lives inside Tags, hence the
		// in-memory pass; Id is identity-ascending, so max Id = newest.
		var slim = await db.HealthReports.Select(r => new { r.Id, r.Svc, r.Tags }).ToListAsync(ct);
		var maxIds = slim
			.GroupBy(r => (r.Svc, Project: HealthTags.Parse(r.Tags).GetValueOrDefault("project", "")))
			.Select(g => g.Max(x => x.Id))
			.ToList();
		var latest = maxIds.Count == 0
			? []
			: await db.HealthReports.Where(r => maxIds.Contains(r.Id)).ToListAsync(ct);

		return new WorkspaceRollup(logCount, dbCount, keyCount, latest);
	}

	public async Task<ProjectRollup> GetProjectRollupAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		var logCount = await db.Logs.CountAsync(l => l.ProjectKey == projectKey, ct);
		var dbCount = await db.DataDbs.CountAsync(d => d.ProjectKey == projectKey, ct);
		var keyCount = await db.ApiKeys.CountAsync(k => k.ProjectKey == projectKey, ct);

		// Latest report per (Svc, raw Tags) — the page's own pre-existing grouping key, distinct from
		// the workspace rollup's (Svc, parsed project tag) above (see the interface doc).
		var maxIds = await db.HealthReports
			.GroupBy(r => new { r.Svc, r.Tags })
			.Select(g => g.Max(x => x.Id))
			.ToListAsync(ct);
		var latest = maxIds.Count == 0
			? []
			: await db.HealthReports.Where(r => maxIds.Contains(r.Id)).ToListAsync(ct);

		return new ProjectRollup(logCount, dbCount, keyCount, latest);
	}

	static async Task<IReadOnlyDictionary<string, int>> CountByProjectAsync(IQueryable<string> projectKeys, CancellationToken ct)
	{
		var rows = await projectKeys
			.GroupBy(k => k)
			.Select(g => new { Key = g.Key, Count = g.Count() })
			.ToListAsync(ct);
		return rows.ToDictionary(r => r.Key, r => r.Count, StringComparer.Ordinal);
	}
}
