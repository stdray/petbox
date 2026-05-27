namespace YobaBox.Core.Settings;

// Per-project log retention. Project owns it (deepestScope); cascades to Workspace
// (default for new projects in this ws) and System (instance-wide default).
//
// Storage scope: Project. Read via:
//   resolver.GetAsync<LogSettings>(Scope.Project, projectKey)
//
// RunInterval is system-only (RetentionService is a singleton background loop,
// can't differ per project), so its TopLevel is System and its scope-key for
// writes is "$".
public sealed record LogSettings
{
	[Setting(TopLevel = Scope.Workspace, Key = "log.retention.days",
		Description = "How long to keep log entries before sweeping. Per-project override, otherwise workspace or system default.")]
	public int RetentionDays { get; init; } = 7;

	[Setting(TopLevel = Scope.System, Key = "log.retention.systemDays",
		Description = "Retention for the $system project (overrides RetentionDays for $system).")]
	public int SystemRetainDays { get; init; } = 30;

	[Setting(TopLevel = Scope.System, Key = "log.retention.runIntervalSeconds",
		Description = "How often the retention sweeper runs (seconds).")]
	public int RunIntervalSeconds { get; init; } = 3600;
}
