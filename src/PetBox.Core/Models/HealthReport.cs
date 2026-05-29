using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// Append-only health/status report for a running thing, identified by (Svc, Tags).
// Pushed by bots (POST /api/health) or pulled by the poller from a HealthEndpoint.
// The status page shows the latest report per (Svc, Tags); history is retained
// then swept. Tags is a canonical comma-separated key:value string (incl project:).
[Table("HealthReports")]
public sealed record HealthReport
{
	[Column, Identity, PrimaryKey, NotNull]
	public long Id { get; init; }

	[Column, NotNull]
	public string Svc { get; init; } = string.Empty;

	[Column, Nullable]
	public string? Name { get; init; }

	[Column, NotNull]
	public string Tags { get; init; } = string.Empty;

	[Column, Nullable]
	public string? Version { get; init; }

	[Column, Nullable]
	public string? Sha { get; init; }

	[Column, Nullable]
	public string? BuildDate { get; init; }

	[Column, NotNull]
	public string Status { get; init; } = string.Empty;

	[Column, NotNull]
	public DateTime ReceivedAt { get; init; }

	// "push" | "pull"
	[Column, NotNull]
	public string Source { get; init; } = "push";
}
