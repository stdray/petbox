namespace PetBox.Log.Core.Data;

// Well-known log identifiers. A log is addressed by (projectKey, logName); the
// file lives at logs/{projectKey}/{logName}.db.
public static class LogNames
{
	// Fallback log name for header-routed ingestion that does not carry an
	// explicit log in the URL path. Must be created explicitly — no auto-vivify.
	public const string Default = "default";

	// The petbox self-log: the one log auto-created at boot. Home project is the
	// system project. Receives petbox's own ILogger output + Seq self-log.
	public const string SelfLog = "petbox";

	// The self-telemetry access-line log: the per-request line RequestLoggingMiddleware emits
	// (EventId 500-503), routed here instead of `petbox` so its high-frequency volume neither
	// drowns out nor is dropped from the rest of self-telemetry (self-telemetry-log-routing).
	// Auto-created at boot next to `petbox`, with its own 14-day retention window.
	public const string AccessLog = "access";

	// Home project for the self-log (the system workspace/project, also used for
	// admin bootstrap — unrelated to log routing otherwise).
	public const string SystemProject = "$system";
}
