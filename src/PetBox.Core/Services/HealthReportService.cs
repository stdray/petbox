using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Services;

// One health report as a CALLER states it: the tags are still a raw dictionary, because
// canonicalising them is the report's own rule, not the caller's — see HealthTags.Canonical, and
// note that (Svc, canonical Tags) is the IDENTITY the status page groups by. A push that
// canonicalised its own tags would be free to canonicalise them differently from the poller's.
public sealed record HealthReportInput(
	string Svc,
	string? Name,
	IReadOnlyDictionary<string, string> Tags,
	string? Version,
	string? Sha,
	string? BuildDate,
	string Status);

// The WRITE door to the append-only `HealthReports` table in core.db, for the PUSH side of the
// health subsystem (POST /api/health). It exists because an endpoint lambda is pipeline code, and
// AGENTS.md is blunt about it: the database is visible only in the service layer — a minimal-API
// handler asks a service, it does not call .Open() itself.
//
// Deliberately narrow: recording a report is the only thing the push endpoint does to core.db. The
// READ/search side of health (the status page, the MCP health tools) still queries its own way and
// is not this door's business.
public interface IHealthReportService
{
	// Append one report, stamped as received now and marked Source = "push". Never an update: the
	// table is a LOG — the status page shows the latest per (Svc, Tags) and retention sweeps the
	// rest — so a second report from the same service is a new row, not an overwrite.
	Task RecordPushAsync(HealthReportInput input, CancellationToken ct = default);
}

public sealed class HealthReportService(ICoreDbFactory dbf) : IHealthReportService
{
	public async Task RecordPushAsync(HealthReportInput input, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await db.InsertAsync(new HealthReport
		{
			Svc = input.Svc.Trim(),
			Name = input.Name,
			Tags = HealthTags.Canonical(input.Tags),
			Version = input.Version,
			Sha = input.Sha,
			BuildDate = input.BuildDate,
			Status = input.Status.Trim(),
			ReceivedAt = DateTime.UtcNow,
			Source = "push",
		}, token: ct);
	}
}
