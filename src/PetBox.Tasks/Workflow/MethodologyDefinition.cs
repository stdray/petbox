namespace PetBox.Tasks.Workflow;

// A project's methodology as DATA — the user-defined counterpart of the hardcoded
// WorkflowCatalog preset (spec methodology-from-primitives). One definition per project,
// stored as a temporal JSON document (MethodologyDefRow) and resolved at runtime through
// MethodologyRuntime: a kind the definition declares OVERRIDES the catalog; every other
// kind (and a project with no definition) keeps the built-in preset.
//
// The shape mirrors what tasks.workflow answers per board: a kind hosts one or more
// workflow blocks, each block is one state machine shared by every type slug in it.
public sealed record MethodologyDefinition(
	string Name,
	IReadOnlyList<MethodologyKindDef> Kinds)
{
	// Definition-level (project-wide) primitives — additive: a stored wave-1 document
	// (no fields in its JSON) deserializes to the empty defaults.
	//
	// Additional relation kinds the project declares, usable in relations.create alongside
	// the builtin process + neutral kinds (spec primitives-link-kinds).
	public IReadOnlyList<MethodologyLinkKindDef> LinkKinds { get; init; } = [];
	// Declared tag namespaces (spec primitives-tag-axes). Empty = free-form tags on
	// definition-resolved boards (the wave-1.2 posture); declared = a namespaced tag must
	// use one of these axes (bare tags are rejected, same as the enforced catalog mode).
	public IReadOnlyList<MethodologyTagAxisDef> TagAxes { get; init; } = [];
}

// One board kind of the methodology. `Kind` is a FREE-FORM slug ([a-z][a-z0-9_-]*), NOT
// the BoardKind enum — user-defined kinds are the point; bridging them onto the enum seam
// is the engine task. `QuickAddAllowed` mirrors WorkflowCatalog.QuickAddAllowed: whether
// the bare board quick-add form may create nodes of this kind.
public sealed record MethodologyKindDef(
	string Kind,
	bool QuickAddAllowed,
	IReadOnlyList<MethodologyWorkflowDef> Workflows)
{
	// Per-type creation link requirements (spec primitives-link-constraints). Default
	// empty — NO requirement is the default; constraints are opt-in per type, never a
	// global law.
	public IReadOnlyList<MethodologyLinkConstraintDef> LinkConstraints { get; init; } = [];
}

// "A NEW node of type `Type` on a board of this kind must carry a link of kind `Link` at
// creation" — the data-driven generalization of the hardcoded work-board RequireSpecLinks
// (work feature/bug must have specRef; chore exempt because no constraint names it).
// `Link` is limited to the kinds expressible IN the upsert call: task_spec (specRef),
// blocks (blockedBy), idea_spec (ideaRef) — post-hoc relation kinds can't gate creation.
// Edits don't re-require the link.
public sealed record MethodologyLinkConstraintDef(string Type, string Link);

// A project-declared relation kind: a free semantic edge with NO FSM effects and no
// process meaning (like the builtin neutral kinds). `Slug` follows the common slug spec
// and must not collide with a builtin kind.
public sealed record MethodologyLinkKindDef(string Slug, string? Description = null);

// A declared tag namespace: on a definition-resolved board with axes declared, every tag
// must be `<Namespace>:value` with the namespace from this list.
public sealed record MethodologyTagAxisDef(string Namespace, string? Description = null);

// One state machine shared by every type slug in `Types` (the tasks.workflow block shape).
// Convention: Statuses[0] is the initial status (same as Workflow.Initial); slug matching
// is case-insensitive.
public sealed record MethodologyWorkflowDef(
	IReadOnlyList<string> Types,
	IReadOnlyList<WorkflowStatus> Statuses,
	IReadOnlyList<MethodologyTransitionDef> Transitions)
{
	public string Initial => Statuses[0].Slug;
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
	string? PreconditionArtifact = null);
