namespace PetBox.Core.Settings;

// Health-poller tuning. System-level only (one HealthPoller singleton per process).
public sealed record DashboardSettings
{
	[Setting(TopLevel = Scope.System, Key = "dashboard.healthPollIntervalSeconds",
		Description = "How often the health poller probes endpoints (seconds, min 5).")]
	public int HealthPollIntervalSeconds { get; init; } = 30;

	[Setting(TopLevel = Scope.System, Key = "dashboard.requestTimeoutSeconds",
		Description = "Per-request timeout when pulling a HealthEndpoint URL (seconds).")]
	public int RequestTimeoutSeconds { get; init; } = 5;

	[Setting(TopLevel = Scope.System, Key = "dashboard.pushTtlSeconds",
		Description = "Max age of the latest health report before the status page marks it stale (seconds).")]
	public int StaleSeconds { get; init; } = 300;

	[Setting(TopLevel = Scope.System, Key = "dashboard.healthRetentionDays",
		Description = "How long to keep health report history before the sweep deletes it (days).")]
	public int HealthRetentionDays { get; init; } = 30;
}
