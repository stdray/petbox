namespace YobaBox.Core.Settings;

// Health-poller tuning. System-level only (one HealthPoller singleton per process).
public sealed record DashboardSettings
{
	[Setting(TopLevel = Scope.System, Key = "dashboard.healthPollIntervalSeconds",
		Description = "How often the health poller probes endpoints (seconds, min 5).")]
	public int HealthPollIntervalSeconds { get; init; } = 30;

	[Setting(TopLevel = Scope.System, Key = "dashboard.requestTimeoutSeconds",
		Description = "Per-request timeout when probing service /health endpoints (seconds).")]
	public int RequestTimeoutSeconds { get; init; } = 5;

	[Setting(TopLevel = Scope.System, Key = "dashboard.pushTtlSeconds",
		Description = "For HealthModel=Push services, max staleness before flipping to Down.")]
	public int PushTtlSeconds { get; init; } = 300;
}
