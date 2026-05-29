using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// A pull-mode health source: petbox periodically GETs Url and stores the returned
// status structure as a HealthReport (Source="pull"). Configured per project in
// admin. Push-mode sources need no row here — they POST to /api/health directly.
[Table("HealthEndpoints")]
public sealed record HealthEndpoint
{
	[Column, Identity, PrimaryKey, NotNull]
	public long Id { get; init; }

	[Column, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	[Column, NotNull]
	public string Url { get; init; } = string.Empty;

	[Column, NotNull]
	public bool Enabled { get; init; } = true;

	[Column, NotNull]
	public int IntervalSeconds { get; init; } = 60;

	[Column, NotNull]
	public DateTime CreatedAt { get; init; }

	[Column, Nullable]
	public string? CreatedBy { get; init; }
}
