using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Health;

// The READ door onto HealthReports (core petbox.db). The push side (PetBox.Web.Health.HealthApi,
// POST /api/health) and the pull side (PetBox.Dashboard.HealthPoller) APPEND rows; everything that
// wants to READ them — the health_search MCP tool today, the dashboard next — comes through here,
// because the database is visible only in the service layer (AGENTS.md).
//
// The PROJECT PREDICATE lives here, welded into the read, and that is the point: a report's project
// is a tag INSIDE the canonical Tags string (HealthTags.Project), so it cannot be a SQL WHERE — every
// caller that filtered it itself was one forgotten line away from serving another project's fleet.
// The caller names a project and gets THAT project's rows; there is no method that hands back the
// unfiltered table.
public interface IHealthReportService
{
	// Every report of `projectKey`, optionally narrowed to one service name. Ordered by Id (identity-
	// ascending = chronological), so the caller's grouping can take the max Id in a group as "latest".
	Task<IReadOnlyList<HealthReport>> ListForProjectAsync(
		string projectKey, string? svc = null, CancellationToken ct = default);
}

public sealed class HealthReportService(ICoreDbFactory dbf) : IHealthReportService
{
	public async Task<IReadOnlyList<HealthReport>> ListForProjectAsync(
		string projectKey, string? svc = null, CancellationToken ct = default)
	{
		using var db = dbf.Open();

		// Svc is a plain column — narrow in SQL when asked. The project tag is not, so it is an
		// in-memory pass over what the Svc filter (or nothing) left.
		var query = db.HealthReports.AsQueryable();
		if (!string.IsNullOrWhiteSpace(svc))
		{
			var s = svc.Trim();
			query = query.Where(r => r.Svc == s);
		}

		var rows = await query.OrderBy(r => r.Id).ToListAsync(ct);
		return [.. rows.Where(r => string.Equals(HealthTags.Project(r.Tags), projectKey, StringComparison.Ordinal))];
	}
}
