namespace PetBox.Tasks.Workflow;

// Role of a board. Drives which task types/statuses/transitions apply and which
// invariants/effects fire. `Simple` (default, formerly `Free`) = a lightweight preset
// with a fixed status/type vocab and free transitions; the methodology kinds add gates;
// `Classic` = the standalone GitHub/Jira/Linear-level status model (simple-like: no
// singleton rule, no auto-wire, free-form tags).
// Simple is first so it stays the ParseKind fallback — a legacy "free" string maps to it.
public enum BoardKind { Simple, Spec, Ideas, Intake, Work, Classic }

// Terminal kind of a status — data that powers UI "closed" predicate + badge,
// and the (capability-level) approve gate (only a maintainer reaches TerminalOk).
public enum StatusKind { Open, TerminalOk, TerminalCancel }

// `Description` (spec methodology-primitive-descriptions): free-form prose, null = none,
// surfaced by the compiled process guide (MethodologyGuide) next to the status slug; never
// resolved or enforced against.
public sealed record WorkflowStatus(string Slug, string Name, StatusKind Kind, string? Description = null);

// A directed edge in a type's state machine. `RequiresApproval` marks the
// transition as maintainer-only (the approve gate — capability modelled here,
// enforcement is opt-in at the call site). `RequiresReason` demands a non-empty
// `reason` field on the status-changing upsert (e.g. triage → wontfix) — never the
// node body. `PreconditionArtifact` names a comment-artifact tag (e.g. "spec_plan" →
// an `artifact:spec_plan` comment) the node must carry before the transition fires —
// gates are transition data, enforced by RequirePreconditionArtifactsAsync (the ideas
// preset gates exploring→review this way).
public sealed record WorkflowTransition(string From, string To, bool RequiresApproval = false, bool RequiresReason = false, string? PreconditionArtifact = null)
{
	// Approval-gate MODE (schema v2): with RequiresApproval, `true` means the server BLOCKS
	// the transition unless the actor can approve (tasks:approve at the MCP door, the
	// cookie-authenticated owner in the UI); `false` keeps owner-only by CONVENTION. The
	// builtin presets never enforce, so live preset behavior is unchanged.
	public bool EnforceApproval { get; init; }

	// Free-text pre-transition conditions (schema v2) — a convention the guide renders and
	// the workflow graph marks; never server-enforced. Carried here so presentation surfaces
	// (the graph's `checklist` edge marker) see it without re-reading the definition.
	public IReadOnlyList<string> Checklist { get; init; } = [];

	// Whether the server actually BLOCKS a missing RequiresReason/PreconditionArtifact for THIS
	// transition (the "force" half — EnforceApproval is approval's; schema v2, spec methodology-
	// gate-strictness). Default true reproduces today's behavior: reason and precondition
	// artifacts are both hard, unconditionally, for every existing definition (none declares
	// Enforce, the new nullable field). false = the gate is a CONVENTION only — the guide still
	// states it, the server does not block it.
	//
	// NOTE this stays a scalar, not a list: MethodologyTransitionDef.RequiredArtifacts (the
	// declaration) CAN name more than one non-inline artifact, but ToWorkflow collapses it to
	// (at most) one inline + one non-inline here — the shape WorkflowTransition already had
	// before this schema (RequiresReason bool + PreconditionArtifact string), and the ONE
	// GuardEngine/WorkflowEngine still enforce at runtime. A settable LIST field here would be
	// included in this record's generated structural equality, and every pre-existing test that
	// hand-builds an expected WorkflowTransition (MethodologyPresetsTests and friends) compares
	// by value without knowing about it — breaking bit-for-bit reproduction for a presentation
	// nicety, not a behavior change. A definition declaring MORE than one non-inline artifact on
	// one transition validates (MethodologyDefinitionValidator) but only the FIRST is actually
	// enforced — a known v1 limitation (documented on RequiredArtifactDef), not a silent runtime
	// surprise: it is caught by inspection of the stored document, same as the pre-existing
	// single-PreconditionArtifact ceiling.
	public bool EnforceArtifacts { get; init; } = true;
}

// A state machine for one task type on a board kind. Convention: Statuses[0] is
// the initial status. Slug matching is case-insensitive.
public sealed record Workflow(string Type, IReadOnlyList<WorkflowStatus> Statuses, IReadOnlyList<WorkflowTransition> Transitions)
{
	public string Initial => Statuses[0].Slug;

	public WorkflowStatus? Status(string slug) =>
		Statuses.FirstOrDefault(s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));

	public bool Has(string slug) => Status(slug) is not null;

	public bool IsTerminal(string slug) =>
		Status(slug) is { Kind: StatusKind.TerminalOk or StatusKind.TerminalCancel };

	public WorkflowTransition? Transition(string from, string to) =>
		Transitions.FirstOrDefault(t =>
			string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase) &&
			string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase));

	public IReadOnlyList<string> NextFrom(string from) =>
		Transitions.Where(t => string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase))
			.Select(t => t.To).ToList();

	public string Slugs() => string.Join("|", Statuses.Select(s => s.Slug));
}
