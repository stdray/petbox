namespace YobaBox.Dashboard;

public sealed record HealthPollerOptions
{
	public int HealthPollIntervalSeconds { get; init; } = 30;
	public int RequestTimeoutSeconds { get; init; } = 5;
	// For HealthModel.Push services, how long since the last CheckedAt before we
	// flip Health=Down. Default 5 minutes — 2× a typical 2-minute heartbeat interval.
	public int PushTtlSeconds { get; init; } = 300;
}
