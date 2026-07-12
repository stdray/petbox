using System.ComponentModel;
using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Mcp;

// Read side of the health subsystem for agents: the latest report per running service,
// with a stale flag and (opt-in) history over a window. The push side is
// PetBox.Web.Health.HealthApi (POST /api/health), the pull side is
// PetBox.Dashboard.HealthPoller; both APPEND HealthReports (identity = (Svc, canonical
// Tags)) into core petbox.db. Until now those reports were only visible through the UI
// dashboard — this tool exposes the same "latest per service" view over MCP.
//
// Time is normalized to ISO-8601 UTC ('…THH:mm:ss.fffZ') ON READ only: HealthReports.ReceivedAt
// is persisted by SQLite as a space-separated text timestamp ('2026-07-02 06:15:34.279'), so
// staleness/age are computed in memory from the materialized DateTime (never a SQL string
// comparison against a 'T'-shaped literal, which silently mismatches the stored space form).
[McpServerToolType]
public static class HealthTools
{
	[McpServerTool(Name = "health_search", Title = "Read service health reports", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(HealthSearchResultView))]
	[Description("Reads the latest health report per running service in a project (status, version, age, stale flag), with time in ISO-8601 UTC. Identity of a service is (svc, canonical tags). Optionally returns per-service history when `window` (seconds) and/or `limit` are supplied. Requires health:read scope; a project-scoped key sees only its own project, a cross-project ('*') key any project.")]
	public static async Task<HealthSearchResultView> SearchAsync(
		IHttpContextAccessor http,
		ICoreDbFactory dbf,
		[Description("Project key — must match the calling ApiKey's project claim (a '*' key may name any project).")] string projectKey,
		[Description("Optional exact service name filter (HealthReport.Svc).")] string? svc = null,
		[Description("A latest report older than this many seconds is flagged stale. Default 300.")] int staleThresholdSeconds = 300,
		[Description("Optional history time window in seconds: history entries received within the last N seconds are returned. Supplying window and/or limit turns history on (default off = latest only).")] int? window = null,
		[Description("Optional cap on history entries per service (most-recent first). Defaults to 50 when history is on.")] int? limit = null,
		CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await ModuleMcp.AssertProject(http, projectKey, ct);
		AssertScope(http, ApiKeyScopes.HealthRead);

		if (staleThresholdSeconds < 0) throw new ArgumentException("staleThresholdSeconds must be >= 0");
		if (window is < 0) throw new ArgumentException("window must be >= 0");
		if (limit is < 0) throw new ArgumentException("limit must be >= 0");

		var withHistory = window.HasValue || limit.HasValue;
		var historyLimit = limit ?? 50;

		// Load candidate rows (optionally narrowed by Svc in SQL — a plain string column, safe
		// to compare). The project tag lives INSIDE the canonical Tags string, so the project
		// filter is an in-memory pass, mirroring the dashboard.
		var query = db.HealthReports.AsQueryable();
		if (!string.IsNullOrWhiteSpace(svc))
		{
			var s = svc.Trim();
			query = query.Where(r => r.Svc == s);
		}
		var rows = await query.ToListAsync(ct);

		var now = DateTime.UtcNow;

		// Keep only this project's rows, then group by the service identity (Svc, canonical Tags).
		// Id is identity-ascending, so max Id in a group = the newest report.
		var groups = rows
			.Where(r => string.Equals(HealthTags.Project(r.Tags), projectKey, StringComparison.Ordinal))
			.GroupBy(r => (r.Svc, r.Tags))
			.OrderBy(g => g.Key.Svc, StringComparer.Ordinal)
			.ThenBy(g => g.Key.Tags, StringComparer.Ordinal);

		var services = new List<HealthServiceView>();
		foreach (var g in groups)
		{
			var latest = g.MaxBy(r => r.Id)!;
			var ageSeconds = AgeSeconds(now, latest.ReceivedAt);

			List<HealthHistoryEntryView>? history = null;
			if (withHistory)
			{
				history = g
					.OrderByDescending(r => r.Id)
					.Select(r => (Report: r, Age: AgeSeconds(now, r.ReceivedAt)))
					.Where(x => !window.HasValue || x.Age <= window.Value)
					.Take(historyLimit)
					.Select(x => new HealthHistoryEntryView(
						Status: x.Report.Status,
						Version: x.Report.Version,
						Sha: x.Report.Sha,
						ReceivedAt: ToIsoUtc(x.Report.ReceivedAt),
						AgeSeconds: x.Age,
						Source: x.Report.Source))
					.ToList();
			}

			services.Add(new HealthServiceView(
				Svc: latest.Svc,
				Name: latest.Name,
				Tags: HealthTags.Parse(latest.Tags).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
				Status: latest.Status,
				Version: latest.Version,
				Sha: latest.Sha,
				ReceivedAt: ToIsoUtc(latest.ReceivedAt),
				AgeSeconds: ageSeconds,
				Stale: ageSeconds > staleThresholdSeconds,
				History: history));
		}

		return new HealthSearchResultView(services);
	}

	// HealthReport.ReceivedAt is written as DateTime.UtcNow but SQLite round-trips it as text
	// with an Unspecified kind; it always denotes UTC wall-clock. Stamp it UTC and emit the
	// canonical ISO form with 'T' + 'Z'.
	static string ToIsoUtc(DateTime receivedAt) =>
		DateTime.SpecifyKind(receivedAt, DateTimeKind.Utc)
			.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

	static long AgeSeconds(DateTime nowUtc, DateTime receivedAt)
	{
		var age = (long)(nowUtc - DateTime.SpecifyKind(receivedAt, DateTimeKind.Utc)).TotalSeconds;
		return age < 0 ? 0 : age; // clock skew guard — a future timestamp is not "negative age"
	}

	static void AssertScope(IHttpContextAccessor accessor, string required)
	{
		var ctx = accessor.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(required, StringComparer.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}
}

// ---- health_search wire shapes (kept in this file so parallel agents editing the shared
// Contract records don't collide) -----------------------------------------------------------

// Latest report for one running service. ReceivedAt is ISO-8601 UTC; AgeSeconds is the
// server-computed age; Stale = AgeSeconds > staleThresholdSeconds. History is null (omitted
// by the serializer) unless the caller asked for it; null Name/Version/Sha are likewise omitted.
public sealed record HealthServiceView(
	string Svc,
	string? Name,
	IReadOnlyDictionary<string, string> Tags,
	string Status,
	string? Version,
	string? Sha,
	string ReceivedAt,
	long AgeSeconds,
	bool Stale,
	IReadOnlyList<HealthHistoryEntryView>? History = null);

// One historical report for a service, most-recent first. Source is "push" | "pull".
public sealed record HealthHistoryEntryView(
	string Status,
	string? Version,
	string? Sha,
	string ReceivedAt,
	long AgeSeconds,
	string Source);

public sealed record HealthSearchResultView(IReadOnlyList<HealthServiceView> Services);
