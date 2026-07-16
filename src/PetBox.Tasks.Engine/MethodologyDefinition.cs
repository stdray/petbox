namespace PetBox.Tasks.Workflow;

// A project's methodology as DATA — the user-defined counterpart of the built-in
// MethodologyPresets (spec methodology-from-primitives). One definition per project,
// stored as a temporal JSON document (MethodologyDefRow) and resolved at runtime through
// MethodologyRuntime: a kind the definition declares OVERRIDES the presets; every other
// kind (and a project with no definition) keeps the built-in preset.
//
// The shape mirrors what tasks_workflow answers per board: a kind hosts one or more
// workflow blocks, each block is one state machine shared by every type slug in it.
public sealed record MethodologyDefinition(
	string Name,
	IReadOnlyList<MethodologyKindDef> Kinds)
{
	// Definition-level primitives — additive: a stored wave-1 document (no fields in its
	// JSON) deserializes to the empty defaults. Live authority is the methodology
	// INSTANCE that carries this document (board → MethodologyInstance membership);
	// the project-singleton MethodologyDefRow remains a transitional dual-read for
	// boards without membership (spec methodology-instance-scoped-axes).
	//
	// Additional relation kinds declared here, usable in relations_create alongside
	// the builtin process + neutral kinds (spec primitives-link-kinds).
	public IReadOnlyList<MethodologyLinkKindDef> LinkKinds { get; init; } = [];
	// Declared tag namespaces (spec primitives-tag-axes). Empty = free-form tags on
	// definition-resolved boards (the wave-1.2 posture); declared = a namespaced tag must
	// use one of these axes (bare tags are rejected, same as the enforced quartet presets).
	public IReadOnlyList<MethodologyTagAxisDef> TagAxes { get; init; } = [];
}

// One board kind of the methodology. `Kind` is a FREE-FORM slug ([a-z][a-z0-9_-]*), NOT
// the BoardKind enum — user-defined kinds are the point; bridging them onto the enum seam
// is the engine task. `QuickAddAllowed` mirrors the preset knob: whether the bare board
// quick-add form may create nodes of this kind.
public sealed record MethodologyKindDef(
	string Kind,
	bool QuickAddAllowed,
	IReadOnlyList<MethodologyWorkflowDef> Workflows)
{
	// Per-type creation link requirements (spec primitives-link-constraints). Default
	// empty — NO requirement is the default; constraints are opt-in per type, never a
	// global law.
	public IReadOnlyList<MethodologyLinkConstraintDef> LinkConstraints { get; init; } = [];
	// Declared transition effects (schema v2): "when a node of THIS kind enters status
	// `On`, set linked nodes to `Set`" — the generalization of the once-hardcoded
	// cross-board automation (intake auto-close, blocks auto-unblock) as DATA. Default
	// empty. Executed by TasksService.RunTransitionEffectsAsync.
	public IReadOnlyList<MethodologyTransitionEffectDef> Effects { get; init; } = [];
	// When set: auto-wire this kind's SpecBoard to the sole active board of
	// `AutoWireSpecFrom` when this kind also has exactly one active board and SpecBoard
	// is empty (the quartet work→spec auto-wire, as data — spec primitives-enum-residual).
	// Null = no auto-wire. Executed by TasksService.AutoWireSpecAsync.
	public string? AutoWireSpecFrom { get; init; }
	// When set: boards of this kind compute a bottom-up delivery status from inbound
	// task_spec links using the declared type roles (the quartet feature/bug roll-up, as
	// data). Null = no delivery computation for this kind.
	public MethodologyDeliveryDef? Delivery { get; init; }
	// The view mode (BoardViewModeNames) a board of this kind opens in when the user has no
	// explicit/saved choice (spec methodology-default-view-field, board-view-persistence).
	// Null = the builtin default (BoardViewModeNames.Tree) applies — so a stored document
	// from before this field existed deserializes fine and behaves exactly as before.
	// Validated against BoardViewModeNames.All by MethodologyDefinitionValidator; an
	// unrenderable-but-known name (e.g. kanban before its partial ships) is a resolve-time
	// silent degradation to Tree, not a definition error.
	public string? DefaultView { get; init; }
	// The outline view's reveal mode (OutlineRevealModeNames) for a board of this kind
	// (board-view-mode-framework). Null = the conservative builtin default (Navigate)
	// applies. Declared HERE, as data on the kind, rather than derived from a process-role
	// enum (BoardKind/PresetKind) — a board provisioned from the quartet/classic BUILTIN
	// TEMPLATE stores its kinds as a materialized MethodologyDefinition (methodology-
	// template-storage: MethodologyPresets.RenderPresetDefinition copies each preset
	// MethodologyKindDef verbatim, INCLUDING this field), so a real `spec` board gets
	// InlineLazy exactly like a pure-preset one — MethodologyRuntime.PresetKind would
	// wrongly read null for it (it treats ANY defined kind as process-role-less, the
	// correct guard for FSM/delivery/quartet-invariant behavior, but wrong for a display
	// heuristic that should travel with the template). Validated against
	// OutlineRevealModeNames.All by MethodologyDefinitionValidator.
	public string? OutlineReveal { get; init; }
}

// Delivery roll-up as DATA (spec primitives-enum-residual): how linked task_spec nodes
// contribute to a board's computed delivery. `RequiredTypes` drive progress (none present
// → not_started; any non-TerminalOk → in_progress; all TerminalOk → candidate for done);
// any `DefectTypes` still Open while requireds are done yield done_with_defects.
public sealed record MethodologyDeliveryDef(
	IReadOnlyList<string> RequiredTypes,
	IReadOnlyList<string> DefectTypes);

// "A NEW node of type `Type` on a board of this kind must carry a link of kind `Link` at
// creation" — the work preset states it as data (feature/bug must have specRef; chore
// exempt because no constraint names it), and a definition kind declares its own.
// `Link` is limited to the kinds expressible IN the upsert call: task_spec (specRef),
// blocks (blockedBy), idea_spec (ideaRef) — post-hoc relation kinds can't gate creation.
// Edits don't re-require the link.
public sealed record MethodologyLinkConstraintDef(string Type, string Link)
{
	// Optional link-target declaration (schema v2): the required link must point at a node
	// of kind `TargetKind` and/or in one of `TargetStatuses` — the generalization of the
	// once-hardcoded ideaRef→accepted-idea guard as data. Cross-kind targets resolve at
	// runtime, enforced at write time by GuardEngine.ValidateLinkTargets. Null = no
	// restriction beyond the link kind itself.
	public string? TargetKind { get; init; }
	public IReadOnlyList<string>? TargetStatuses { get; init; }
}

// One declared transition effect of a kind (schema v2, spec engine-v2). Fires when a node
// of the OWNING kind ENTERS status `On`: every node linked through relation kind `Link`
// in `Direction` (incoming = the linked node points at this one, outgoing = this node
// points at the linked one) is set to status `Set`; `OnlyFrom` optionally restricts the
// effect to linked nodes currently in that status. `Set`/`OnlyFrom` name statuses of the
// LINKED node's kind — cross-kind, so they are format-checked only and resolve at
// runtime. Executed by TasksService.RunTransitionEffectsAsync when the node enters `On`.
public sealed record MethodologyTransitionEffectDef(
	string On,
	string Link,
	string Direction,
	string Set,
	string? OnlyFrom = null);

// A project-declared relation kind: a free semantic edge with NO FSM effects and no
// process meaning (like the builtin neutral kinds). `Slug` follows the common slug spec
// and must not collide with a builtin kind.
public sealed record MethodologyLinkKindDef(string Slug, string? Description = null);

// A declared tag namespace: on a definition-resolved board with axes declared, every tag
// must be `<Namespace>:value` with the namespace from this list.
public sealed record MethodologyTagAxisDef(string Namespace, string? Description = null);

// One state machine shared by every type slug in `Types` (the tasks_workflow block shape).
// Convention: Statuses[0] is the initial status (same as Workflow.Initial); slug matching
// is case-insensitive.
public sealed record MethodologyWorkflowDef(
	IReadOnlyList<string> Types,
	IReadOnlyList<WorkflowStatus> Statuses,
	IReadOnlyList<MethodologyTransitionDef> Transitions)
{
	public string Initial => Statuses[0].Slug;

	// Map this block onto the shared FSM vocabulary as one type's workflow, carrying the
	// transition gates (approval/reason/precondition artifact) and the approval-gate mode.
	public Workflow ToWorkflow(string type) =>
		new(type, Statuses,
			Transitions.Select(t => new WorkflowTransition(t.From, t.To, t.RequiresApproval, t.RequiresReason, t.PreconditionArtifact)
			{
				EnforceApproval = t.EnforceApproval,
				Checklist = t.Checklist,
			}).ToList());
}

// A directed FSM edge. `PreconditionArtifact` names a comment-artifact tag (e.g.
// "spec_plan") that must exist on the node (as an `artifact:<slug>` comment) before the
// transition fires — the exploring→review gate the catalog enforces imperatively in the
// service, expressed as data and enforced by RequirePreconditionArtifactsAsync.
public sealed record MethodologyTransitionDef(
	string From,
	string To,
	bool RequiresApproval = false,
	bool RequiresReason = false,
	string? PreconditionArtifact = null)
{
	// Approval-gate MODE (schema v2), meaningful only with RequiresApproval: true = the
	// server BLOCKS the transition for a non-owner (the WorkflowEngine enforceApproval
	// capability, wired up by the engine task); false = owner-only by CONVENTION — the
	// guide states the rule, the server does not block (the historical v1 posture, so
	// existing documents keep behavior).
	public bool EnforceApproval { get; init; }
	// Free-text conditions to confirm before this transition (schema v2). Rendered by the
	// guide as a checklist; never enforced by the server — a convention, not a gate.
	public IReadOnlyList<string> Checklist { get; init; } = [];
}
