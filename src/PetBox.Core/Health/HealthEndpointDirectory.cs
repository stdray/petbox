using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Health;

// THE door onto `HealthEndpoints` (core petbox.db) — the pull-mode source list an admin configures
// per project. It is a DIFFERENT TABLE from HealthReports, which is what IHealthReportService (in
// this same namespace, deliberately) owns: reports are what the endpoints PRODUCE.
//
// Why a second interface rather than more methods on IHealthReportService: `one health door, not
// two` (commit ce12100) was about two services opening the SAME table from two namespaces, so a
// caller could find half the API and never learn the other half existed. That failure is about ONE
// TABLE with TWO DOORS. This is the opposite shape — a second table that had NO door at all, and
// whose only reader was a page opening core.db by hand. Both doors live in PetBox.Core.Health, so a
// caller who finds one finds the other; neither can be reached without the other being visible.
//
// The PROJECT is welded into every statement, including the DELETE. That is the point of the
// extraction, not a detail: ProjectDetail used to delete by `Id == id && ProjectKey == ProjectKey`
// inline, and every page that re-derives that predicate is one forgotten conjunct away from letting
// a forged POST delete another project's endpoint (AGENTS.md: "that is how ten copies of 'is this
// project in this route's workspace?' drifted into an IDOR nobody noticed"). Here it is written
// once, inside the statement, so no caller can render one project's list and mutate another's row.

// A URL that is not an absolute URI is refused BEFORE the insert, and the refusal carries its reason
// to the user — a pull endpoint the poller can never GET is not a row anyone wants stored.
public abstract record HealthEndpointAddResult
{
	HealthEndpointAddResult() { }

	public sealed record Added(HealthEndpoint Endpoint) : HealthEndpointAddResult;
	public sealed record Refused(string Reason) : HealthEndpointAddResult;
}

public interface IHealthEndpointDirectory
{
	// This project's configured pull endpoints, ordered by Url (the order the admin table renders).
	Task<IReadOnlyList<HealthEndpoint>> ListForProjectAsync(string projectKey, CancellationToken ct = default);

	// Add one pull endpoint to `projectKey`. The interval floor (>= 5s, default 60s) lives here rather
	// than in the form: it is a property of what the poller can honour, not of how the page renders.
	Task<HealthEndpointAddResult> AddAsync(
		string projectKey, string url, int? intervalSeconds, string? createdBy, CancellationToken ct = default);

	// Delete endpoint `id` — but only if it belongs to `projectKey`. False when it does not exist OR is
	// not this project's; the caller cannot tell those apart, which is exactly the point (no existence
	// oracle for another project's rows). Ownership is proven INSIDE the DELETE, so no TOCTOU window
	// opens between a check and the write.
	Task<bool> DeleteAsync(long id, string projectKey, CancellationToken ct = default);
}

public sealed class HealthEndpointDirectory(ICoreDbFactory dbf) : IHealthEndpointDirectory
{
	public const int MinIntervalSeconds = 5;
	public const int DefaultIntervalSeconds = 60;

	public async Task<IReadOnlyList<HealthEndpoint>> ListForProjectAsync(
		string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.HealthEndpoints
			.Where(e => e.ProjectKey == projectKey)
			.OrderBy(e => e.Url)
			.ToListAsync(ct);
	}

	public async Task<HealthEndpointAddResult> AddAsync(
		string projectKey, string url, int? intervalSeconds, string? createdBy, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out _))
			return new HealthEndpointAddResult.Refused("A valid absolute URL is required.");

		var row = new HealthEndpoint
		{
			ProjectKey = projectKey,
			Url = url.Trim(),
			Enabled = true,
			IntervalSeconds = intervalSeconds is { } s && s >= MinIntervalSeconds ? s : DefaultIntervalSeconds,
			CreatedAt = DateTime.UtcNow,
			CreatedBy = createdBy,
		};

		using var db = dbf.Open();
		await db.InsertAsync(row, token: ct);
		return new HealthEndpointAddResult.Added(row);
	}

	public async Task<bool> DeleteAsync(long id, string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.HealthEndpoints
			.Where(e => e.Id == id && e.ProjectKey == projectKey)
			.DeleteAsync(ct) > 0;
	}
}
