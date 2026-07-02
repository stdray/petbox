namespace PetBox.Tasks.Workflow;

// Role of a board. Drives which task types/statuses/transitions apply and which
// invariants/effects fire. `Simple` (default, formerly `Free`) = a lightweight preset
// with a fixed status/type vocab and free transitions; the methodology kinds add gates.
// Simple is first so it stays the ParseKind fallback — a legacy "free" string maps to it.
public enum BoardKind { Simple, Spec, Ideas, Intake, Work }

// Terminal kind of a status — data that powers UI "closed" predicate + badge,
// and the (capability-level) approve gate (only a maintainer reaches TerminalOk).
public enum StatusKind { Open, TerminalOk, TerminalCancel }

public sealed record WorkflowStatus(string Slug, string Name, StatusKind Kind);

// A directed edge in a type's state machine. `RequiresApproval` marks the
// transition as maintainer-only (the approve gate — capability modelled here,
// enforcement is opt-in at the call site). `RequiresReason` demands a non-empty
// body (e.g. triage → wontfix). `PreconditionArtifact` names a comment-artifact tag
// (e.g. "spec_plan" → an `artifact:spec_plan` comment) the node must carry before the
// transition fires — gates are transition data; the catalog presets leave it null (the
// idea-review gate stays hardcoded in the service until the presets move to data).
public sealed record WorkflowTransition(string From, string To, bool RequiresApproval = false, bool RequiresReason = false, string? PreconditionArtifact = null);

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
