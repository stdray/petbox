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

	// Home project for the self-log (the system workspace/project, also used for
	// admin bootstrap — unrelated to log routing otherwise).
	public const string SystemProject = "$system";
}
