namespace PetBox.Core.Settings;

// Whether a workspace may fall back to the SYSTEM LLM registry when it declares no routes of its
// own (spec: llm-registry-own-store). Two INDEPENDENT switches, exactly like
// SessionFullScanSettings — "the wider scope is the STRICTER one":
//
//   SystemShared     (System scope)    — the owner's tap: is the $system registry offered to other
//                                        workspaces AT ALL? Off => nobody inherits, no exceptions.
//   WorkspaceInherits (Workspace scope) — this workspace's own opt-out: I do not want to fall back
//                                        to the system registry (e.g. I must only ever call my own
//                                        endpoints, with my own keys).
//
// inheritAllowed = SystemShared AND WorkspaceInherits — read as TWO separate resolver calls, NOT as
// one cascading property, so a workspace cannot turn on something the owner switched off, and the
// owner turning the tap on cannot silently re-enable a workspace that opted out:
//
//   resolver.GetAsync<LlmRegistryInheritanceSettings>(Scope.System, "$")    -> .SystemShared
//   resolver.GetAsync<LlmRegistryInheritanceSettings>(Scope.Workspace, ws)  -> .WorkspaceInherits
//
// Both default TRUE (unlike SessionFullScan's opt-in defaults): inheriting the system registry is
// the normal case — the alternative is a workspace with no LLM at all, which is precisely the
// silent breakage this whole move exists to end. When inheritance is refused the chain STOPS at the
// workspace: the caller gets an honest "no route ... (system registry not inherited)", never a quiet
// fallback.
public sealed record LlmRegistryInheritanceSettings
{
	[Setting(TopLevel = Scope.System, HasMinScope = true, MinScope = Scope.System, Key = "llm.registry.inherit.systemShared",
		Description = "Offer the system LLM registry to other workspaces. Off = no workspace inherits it, whatever its own switch says.")]
	public bool SystemShared { get; init; } = true;

	[Setting(TopLevel = Scope.Workspace, HasMinScope = true, MinScope = Scope.Workspace, Key = "llm.registry.inherit.enabled",
		Description = "Let this workspace fall back to the system LLM registry when it declares no routes of its own (requires the system switch too).")]
	public bool WorkspaceInherits { get; init; } = true;
}
