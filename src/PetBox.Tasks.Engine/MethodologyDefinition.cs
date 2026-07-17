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
	// Definition-level strictness default (spec methodology-gate-strictness): the fallback a
	// transition's approval gate resolves to when IT does not declare its own `Enforce.Approval`
	// (a transition-level Enforce always wins — see MethodologyTransitionDef.EffectiveEnforceApproval).
	// Default false reproduces today's behavior — the builtin quartet/classic presets never set
	// this, and every already-stored definition (missing this field entirely) deserializes to
	// false, so approval stays owner-only-by-convention exactly as before. Applies ONLY to kinds
	// THIS definition declares (MethodologyRuntime.StrictMode) — a kind resolved from the builtin
	// presets (this definition doesn't declare it) is never strict, so turning this on for a
	// project cannot retroactively toughen a preset kind the project hasn't authored.
	public bool StrictMode { get; init; }
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
	// Process-role cardinality (spec methodology-kind-singleton): true = at most one open
	// board of this kind per methodology instance (or per legacy null-membership bucket).
	// Null = no opinion from THIS declaration — resolved with the SAME field-level merge as
	// DefaultView/OutlineReveal above, NOT a whole-object merge (ResolvedKind): a builtin-
	// provisioned instance materializes each preset MethodologyKindDef VERBATIM at creation
	// time (RenderPresetDefinition), so an instance created before this field existed stores
	// it as the JSON-missing default on a kind that IS a process role (e.g. `work`) — a
	// whole-object merge would silently read that as "not singleton" on every already-
	// provisioned project (the exact board-view-defaults-not-applied-existing-instances
	// trap). Was gated on membership in the `BoardKind` enum via two duplicate arrays
	// (`Methodological` in TasksService, `ProcessRoleKinds` in MethodologyInstanceService) —
	// a custom-declared kind could never opt in. Now data: the definition author declares it
	// per kind, custom kinds included (spec methodology-kind-singleton).
	public bool? Singleton { get; init; }
	// Free-form prose about this kind (spec methodology-primitive-descriptions): data, not
	// code — MAY be null (most kinds carry none). The compiled process guide
	// (MethodologyGuide.Render, tasks_methodology_guide) surfaces it; nothing resolves or
	// enforces against it. No preset-fallback merge needed (unlike Singleton/BlocksGate/
	// DefaultView above): null legitimately means "no description", not "defer to the
	// preset's opinion" — there is no behavioral default to silently lose on an old stored
	// document.
	public string? Description { get; init; }
	// Blocking-gate statuses (spec methodology-blocks-gate-data): `Status` is the status a node
	// of this kind must name a blocker to enter/hold ("a Blocked task must name a blocker" — a
	// STATE invariant, checked on every write including birth, not a transition gate); `ReleaseTo`
	// is the status a released node moves to. Null = this kind has no blocking gate at all (most
	// kinds). Was two code-level literals ("Blocked"/"InProgress") in
	// GuardEngine.RequireBlockers and TasksService.CloseBlocksOnLeaveAsync, gated on
	// MethodologyRuntime.IsWorkKind — now data, resolved through
	// MethodologyRuntime.BlocksGate(kindSlug) with the SAME field-level merge as Singleton just
	// above (RenderPresetDefinition materializes this verbatim, so a pre-field instance must still
	// read the preset's opinion, not "no gate"). A custom-declared kind can opt into the same
	// invariant under its own status names — the invariant is no longer work's alone by
	// construction, only by which kinds today declare this field.
	public MethodologyBlocksGateDef? BlocksGate { get; init; }
	// The preferred board NAME for this kind (spec primitives-enum-residual, "имя доски выводится
	// из слага вида" — a template couldn't give a board its own name/display, only the kind slug
	// via PickBoardName). Null = no opinion — PickBoardName falls back to the slug-derived
	// candidates exactly as before. When set, tried FIRST by PickBoardName (still subject to the
	// same collision/"node"-reserved rules as every other candidate), so a custom-declared kind
	// can name its board something other than its own slug (e.g. kind `issue` naming its board
	// `backlog`). Resolved through MethodologyRuntime.BoardName with the SAME field-level merge
	// as DefaultView/OutlineReveal/Singleton/BlocksGate above — display-only, not process
	// semantics, and no preset declares it today (every preset's board name already equals its
	// kind slug), so the preset half of the merge is currently always null.
	public string? BoardName { get; init; }
}

// The blocking-gate statuses of a kind (spec methodology-blocks-gate-data): `Status` gates
// GuardEngine.RequireBlockers (a node in this status must name a blocker) and is the trigger
// TaskTransitionEffects consumes incoming `blocks` edges on LEAVING (Effect.onLeave — "history
// kept", no forced status); `ReleaseTo` is the status the kind's own Done-triggered
// last-blocker-release Effect (declared in Effects, not derived here) sets a released node to.
public sealed record MethodologyBlocksGateDef(string Status, string ReleaseTo);

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
	// Free-form prose about why this constraint exists (spec methodology-primitive-
	// descriptions). Null = none. Surfaced by the compiled process guide alongside the
	// constraint's cadence sentence; never enforced.
	public string? Description { get; init; }
}

// One declared transition effect of a kind (schema v2, spec engine-v2; `OnLeave` added by
// methodology-blocks-gate-data). Fires when a node of the OWNING kind ENTERS status `On`
// (default), or — when `OnLeave` is true (the Effect.onLeave primitive) — when it LEAVES
// status `On`: either way, every node linked through relation kind `Link` in `Direction`
// (incoming = the linked node points at this one, outgoing = this node points at the linked
// one) is set to status `Set`; `OnlyFrom` optionally restricts the effect to linked nodes
// currently in that status. `Set` is OPTIONAL: null declares a PURE edge-consumption effect
// (no status propagated to the linked node). `Set`/`OnlyFrom` name statuses of the LINKED
// node's kind — cross-kind, so they are format-checked only and resolve at runtime. Executed
// by TaskTransitionEffects.RunTransitionEffectsAsync.
//
// NOT wired into WorkKind's own preset (methodology-blocks-gate-data review finding): the
// manual-leave-Blocked unblock stays TasksService.CloseBlocksOnLeaveAsync, an IMPERATIVE
// method, deliberately NOT a declared entry on the builtin `work` kind. MethodologyRuntime.
// Effects(kindSlug) resolves WHOLE-OBJECT (unlike BlocksGate/Singleton/DefaultView's
// field-level merge) — a declared kind reads ONLY its own stored Effects, never falling back
// to the preset's for anything the stored copy lacks. A real quartet-provisioned project
// materialized `work` as a defined kind with its Effects list frozen BEFORE this primitive
// existed, so an entry added to the preset here would be invisible on every such project,
// reachable only on a bare/undeclared-kind board — caught in review before it shipped. The
// primitive is proven at the SCHEMA/wire level (MethodologyDefinitionValidator,
// MethodologyKindContractParityTests' round-trip) and its trigger logic is generalized in
// TaskTransitionEffects.RunTransitionEffectsAsync; no integration test yet exercises an
// OnLeave effect end-to-end on a project-DECLARED kind (only the builtin quartet's
// unconditional CloseBlocksOnLeaveAsync is covered) — a project opting a custom kind into
// this primitive is unverified beyond the pure trigger-selection logic.
public sealed record MethodologyTransitionEffectDef(
	string On,
	string Link,
	string Direction,
	string? Set,
	string? OnlyFrom = null,
	bool OnLeave = false,
	// Free-form prose about why this effect exists (spec methodology-primitive-descriptions).
	// Null = none. Appended to EffectSentence wherever effects render; never itself executed.
	string? Description = null);

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
	// transition gates (approval/requiredArtifacts) and their resolved strictness (schema v2,
	// spec methodology-gate-strictness). `strictMode` is the OWNING definition's default (false
	// for every builtin preset and for a definition that doesn't declare it) — passed in rather
	// than read off a field here because a MethodologyWorkflowDef has no back-reference to its
	// definition; callers resolving a DEFINITION-declared kind pass the definition's StrictMode
	// (MethodologyRuntime.StrictMode), preset resolution always passes the default false.
	public Workflow ToWorkflow(string type, bool strictMode = false) =>
		new(type, Statuses,
			Transitions.Select(t =>
			{
				var artifacts = t.EffectiveRequiredArtifacts();
				var reasonArtifact = artifacts.Any(a => a.Inline);
				var preconditionArtifact = artifacts.FirstOrDefault(a => !a.Inline)?.Slug;
				return new WorkflowTransition(t.From, t.To, t.RequiresApproval, reasonArtifact, preconditionArtifact)
				{
					EnforceApproval = t.EffectiveEnforceApproval(strictMode),
					EnforceArtifacts = t.EffectiveEnforceArtifacts(),
					Checklist = t.Checklist,
				};
			}).ToList());
}

// A directed FSM edge. `PreconditionArtifact` names a comment-artifact tag (e.g.
// "spec_plan") that must exist on the node (as an `artifact:<slug>` comment) before the
// transition fires — the exploring→review gate the catalog enforces imperatively in the
// service, expressed as data and enforced by RequirePreconditionArtifactsAsync.
//
// `RequiresReason`/`PreconditionArtifact` are the LEGACY wire shape (kept so an already-stored
// document — the live quartet included — keeps deserializing and behaving exactly as before;
// see EffectiveRequiredArtifacts). New authoring should declare `RequiredArtifacts` instead
// (spec methodology-gate-strictness): `reason` collapses into an artifact with slug "reason",
// `inline:true` — there is no separate reason gate. The validator rejects mixing both shapes on
// one transition, so there is exactly one source of truth per transition, old or new.
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
	// existing documents keep behavior). LEGACY — see EffectiveEnforceApproval; a transition
	// declaring `Enforce.Approval` overrides this.
	public bool EnforceApproval { get; init; }
	// Free-text conditions to confirm before this transition (schema v2). Rendered by the
	// guide as a checklist; never enforced by the server — a convention, not a gate.
	public IReadOnlyList<string> Checklist { get; init; } = [];
	// Free-form prose about this transition (spec methodology-primitive-descriptions). Null =
	// none. Surfaced by the compiled process guide as a note alongside the transition's other
	// marks; never enforced.
	public string? Description { get; init; }

	// The unified gate declaration (schema v2, spec methodology-gate-strictness): every comment
	// artifact this transition needs, `reason` (inline, content riding NodePatch.Reason on the
	// SAME call) or a pre-existing one like `spec_plan` (non-inline, an `artifact:<slug>`
	// comment that must already exist). Empty = declare via the legacy fields instead (or no
	// artifact gate at all). See EffectiveRequiredArtifacts for the merge with the legacy shape;
	// MethodologyDefinitionValidator rejects declaring both on one transition, and rejects
	// `inline:true` for any slug other than "reason" (v1: no other call field carries inline
	// content). KNOWN v1 CEILING: only ONE inline + ONE non-inline artifact is actually enforced
	// per transition at runtime — WorkflowTransition (the resolved shape WorkflowEngine/
	// GuardEngine judge against) carries RequiresReason/PreconditionArtifact, the same single-
	// scalar shape it always has (see the note on WorkflowTransition.EnforceArtifacts for why a
	// list isn't carried there too). A transition declaring more validates and stores fine; only
	// its first inline and first non-inline entry are ever server-enforced.
	public IReadOnlyList<RequiredArtifactDef> RequiredArtifacts { get; init; } = [];
	// The strictness override for THIS transition's gates (schema v2): null = fall through to
	// the defaults (approval → the owning definition's StrictMode; artifacts → always hard —
	// see EffectiveEnforceApproval/EffectiveEnforceArtifacts). Declaring the object but leaving
	// a field null still falls through for THAT field only (GateEnforcementDef fields are
	// independently nullable).
	public GateEnforcementDef? Enforce { get; init; }

	// The resolved requiredArtifacts view: the new-style declaration when non-empty, else the
	// legacy fields translated 1:1 (RequiresReason → {slug:"reason", inline:true},
	// PreconditionArtifact → {slug:PreconditionArtifact, inline:false}) — the "reason IS an
	// artifact, no separate gate" collapse from the outside, whichever shape produced it.
	public IReadOnlyList<RequiredArtifactDef> EffectiveRequiredArtifacts()
	{
		if (RequiredArtifacts.Count > 0) return RequiredArtifacts;
		if (!RequiresReason && PreconditionArtifact is null) return [];
		var legacy = new List<RequiredArtifactDef>(2);
		if (RequiresReason) legacy.Add(new RequiredArtifactDef("reason", Inline: true));
		if (PreconditionArtifact is not null) legacy.Add(new RequiredArtifactDef(PreconditionArtifact, Inline: false));
		return legacy;
	}

	// The EFFECTIVE approval-enforcement decision (declaration separated from its force, spec
	// methodology-gate-strictness): this transition's own `Enforce.Approval` when set; else the
	// legacy per-transition `EnforceApproval` OR the owning definition's `strictMode` (either one
	// demands the capability — matches WorkflowEngine's historical "global flag OR per-transition
	// flag" OR-not-AND posture, `strictMode` just replaces the caller-supplied global flag as the
	// definition's own default). Every existing document has EnforceApproval=false and (now)
	// strictMode=false, so this stays false everywhere it does today.
	public bool EffectiveEnforceApproval(bool strictMode) => Enforce?.Approval ?? (EnforceApproval || strictMode);

	// The EFFECTIVE artifacts-enforcement decision: this transition's own `Enforce.Artifacts`
	// when set; else ALWAYS true — reason and precondition artifacts are both hard today
	// (WorkflowEngine.Validate / GuardEngine.RequirePreconditionArtifacts), unconditionally, with
	// no existing switch — so an undeclared Enforce reproduces exactly that.
	public bool EffectiveEnforceArtifacts() => Enforce?.Artifacts ?? true;
}

// One required comment-artifact of a transition's requiredArtifacts gate (schema v2, spec
// methodology-gate-strictness — the union of the legacy RequiresReason/PreconditionArtifact
// pair). `Slug` names the artifact tag (`artifact:<slug>` comment); `Inline` = true means its
// content rides the SAME call (today only `reason`, via NodePatch.Reason — MethodologyDefinitionValidator
// rejects `inline:true` for any other slug in v1: no other call field carries inline content).
// A non-inline artifact must already exist as a comment before the transition fires.
public sealed record RequiredArtifactDef(string Slug, bool Inline = false);

// What the server actually BLOCKS for one transition's gates (schema v2, spec methodology-gate-
// strictness) — the "force" half of "declaration separated from its force". Both fields are
// independently nullable: null on either means "no opinion, fall through to that field's own
// default" (Approval → the owning definition's StrictMode; Artifacts → always true) — declaring
// the object at all does not implicitly set the OTHER field's default to something else.
public sealed record GateEnforcementDef(bool? Approval = null, bool? Artifacts = null);
