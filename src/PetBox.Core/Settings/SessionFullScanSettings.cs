namespace PetBox.Core.Settings;

// Permission gate for the session-search full-scan escape hatch (spec:
// session-fullscan-optin): a raw, untokenized substring scan over every session's content —
// expensive (full hydration per scanned session) and never run unless BOTH switches below
// allow it AND the caller explicitly opts in per-call (session_search `fullScan:true`).
// Deliberately TWO INDEPENDENT properties, not one cascading setting: a project must not be
// able to unilaterally enable a feature an operator disabled system-wide, and an operator
// flipping the system switch must not silently turn it on for every project that never
// asked for it. Both default OFF.
//
// Read via TWO separate calls (mirrors LogSettings' System/Project pair):
//   resolver.GetAsync<SessionFullScanSettings>(Scope.System, "$")        → .SystemEnabled
//   resolver.GetAsync<SessionFullScanSettings>(Scope.Project, projectKey) → .ProjectEnabled
// allowed = system.SystemEnabled && project.ProjectEnabled.
public sealed record SessionFullScanSettings
{
	[Setting(TopLevel = Scope.System, Key = "search.sessions.fullscan.systemEnabled",
		Description = "System-wide switch for the session-search full-scan escape hatch. Off by default; both this AND the project's own switch must be on.")]
	public bool SystemEnabled { get; init; } = false;

	[Setting(TopLevel = Scope.Project, HasMinScope = true, MinScope = Scope.Project, Key = "search.sessions.fullscan.enabled",
		Description = "Per-project opt-in for the session-search full-scan escape hatch (requires the system switch too).")]
	public bool ProjectEnabled { get; init; } = false;
}
