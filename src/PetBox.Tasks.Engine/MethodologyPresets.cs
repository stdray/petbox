namespace PetBox.Tasks.Workflow;

// The built-in processes — the methodology quartet (intake|ideas|spec|work) and the
// standalone `simple` and `classic` kinds — expressed as PRESET METHODOLOGY DEFINITIONS: the same
// MethodologyDefinition shapes an instance, a template or the project's utility layer can
// store, constructed in code (spec primitives-preset-quartet). This replaces the hardcoded
// WorkflowCatalog 1:1, and the wave-2 primitives that used to be imperative service code are
// now preset DATA:
//   - work linkConstraints: feature/bug must carry task_spec (specRef) at creation;
//     chore has NO constraint — that IS the chore exemption, as data;
//   - ideas: exploring→review carries preconditionArtifact "spec_plan" (the idea-review
//     gate, enforced by RequirePreconditionArtifactsAsync like any definition gate);
//   - tag axes: the quartet kinds run on the builtin area/concern axes; simple declares
//     none — axes-emptiness = free-form tags is the ONE rule for every kind.
// MethodologyRuntime falls back here for any kind slug the BOARD'S WORLD document (an
// instance's rules, or the project's `$utility` layer) does not declare, so preset boards
// behave exactly as before while a document overrides per kind. Presets are the BASELINE, not
// a methodology anything installs silently: a live instance is born only from an EXPLICIT
// tasks_methodology_create (source `builtin`|`template`|`instance` + sourceKey).
public static class MethodologyPresets
{
	// The document `name` the presets-only guide reports when no open instance applies
	// (tasks_methodology_guide, source "presets" — TasksService.PresetsGuide).
	public const string Name = "builtin-presets";

	// Kind slug → process-role enum. The enum survives as the key for what is NOT expressed as
	// document data: which preset KindDef answers for a kind no document declares, the
	// quartet compat surface (EnableMethodologyAsync/GetMethodologyAsync behind the admin
	// enable UI), and UI kind rendering. The per-kind semantics that used to need it are DATA
	// on KindDef: auto-wire (AutoWireFrom — it also drives the wiredBoard check), delivery
	// type roles (Delivery), the one-open-board-per-world rule (Singleton), the blocker gate
	// (BlocksGate) and the ideaRef/specRef target checks (LinkConstraints.TargetKind). Unknown
	// slugs — including the legacy `free` (pre-M029 rows) — read as Simple, as they always did.
	public static BoardKind ParseKind(string? kind) =>
		Enum.TryParse<BoardKind>(kind, ignoreCase: true, out var k) ? k : BoardKind.Simple;

	// ---- the preset kinds as definition data ----

	// SIMPLE (formerly `free`; interim dogfood, not a PetBox promise). A minimal lifecycle
	// with FREE transitions: Todo→InProgress→Done(+Cancelled), Blocked optional. Transitions
	// are all-pairs (any valid status → any), so the generic engine yields free transitions
	// while still rejecting an out-of-vocab status. Type is a label only (one workflow for
	// all simple types); the first type (`task`) is the empty-type/quick-add default.
	static readonly WorkflowStatus[] SimpleStatuses =
	[
		new("Todo", "Todo", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("Blocked", "Blocked", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
	];

	static readonly MethodologyKindDef SimpleKind = new("simple", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["task", "bug", "feature", "chore", "issue"], SimpleStatuses, AllPairs(SimpleStatuses)),
	]);

	// Every ordered (from→to) pair with from≠to — models "free transitions" for a kind.
	static List<MethodologyTransitionDef> AllPairs(IReadOnlyList<WorkflowStatus> statuses) =>
		(from a in statuses
		 from b in statuses
		 where !string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase)
		 select new MethodologyTransitionDef(a.Slug, b.Slug)).ToList();

	// CLASSIC (spec preset-classic) — a single-kind status model at the level of the
	// GitHub/Jira/Linear defaults: Backlog/Todo (Linear + GitHub Projects), InProgress (all
	// three), Review (Linear's default started status; GitHub models review outside
	// Issues), Done, and the not-delivered pair Cancelled/Duplicate (GitHub close reasons
	// "not planned"/"duplicate", Linear's Canceled/Duplicate). Transitions are FREE among
	// the OPEN statuses (Jira's default workflow allows all transitions; Linear/GitHub
	// don't gate status moves — low ceremony wins); terminals are reached EXPLICITLY, with
	// a reason required only INTO Duplicate (a duplicate without a pointer to the original
	// is useless; Cancelled needs none — GitHub closes "not planned" without a mandatory
	// reason), and a closed node reopens to Todo (the GitHub reopen). ONE owner gate: Done
	// is reachable ONLY from Review (Backlog/Todo/InProgress cannot jump straight to Done),
	// mirroring the "agent ceiling is Review" rule the PetBox protocol teaches everywhere
	// else — an agent that never sees that rule spelled out as a synonym still can't self-
	// close from an open, non-review status. The gate is the SAME soft shape as the
	// quartet's `work` kind (Review -> Done, RequiresApproval, no Enforce/EnforceApproval):
	// owner-only by CONVENTION, the server does not block it (methodology-gate-strictness).
	// No link constraints, no effects, no checklists, free-form tags — and NO quartet
	// semantics (no singleton rule, no auto-wire), same as `simple`.
	static readonly WorkflowStatus[] ClassicStatuses =
	[
		new("Backlog", "Backlog", StatusKind.Open),
		new("Todo", "Todo", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("Review", "Review", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
		new("Duplicate", "Duplicate", StatusKind.TerminalCancel),
	];

	// Classic's edge set: all ordered pairs among the OPEN statuses (free movement); every
	// open status may close into Cancelled (ungated) or Duplicate (reason required — the
	// pointer to the original); Done is reached ONLY from Review, owner-only by convention
	// (same soft shape as WorkKind's Review -> Done just below); each terminal reopens to Todo.
	static List<MethodologyTransitionDef> ClassicTransitions()
	{
		var open = ClassicStatuses.Where(s => s.Kind == StatusKind.Open).Select(s => s.Slug).ToList();
		var edges = new List<MethodologyTransitionDef>();
		foreach (var from in open)
			foreach (var to in open.Where(t => t != from))
				edges.Add(new(from, to));
		foreach (var from in open)
		{
			edges.Add(new(from, "Cancelled"));
			edges.Add(new(from, "Duplicate", RequiresReason: true));
		}
		edges.Add(new("Review", "Done", RequiresApproval: true)); // owner-only, convention (server doesn't block)
		foreach (var terminal in new[] { "Done", "Cancelled", "Duplicate" })
			edges.Add(new(terminal, "Todo"));
		return edges;
	}

	// ONE block for every type: task|feature|bug are labels over the same FSM (owner
	// review: two identical state machines are one state machine — the former bug-only
	// repro checklist left the preset for a deliberation idea, and with it the only reason
	// to split). Type order matters: task is first ⇒ the quick-add/untyped default.
	static readonly MethodologyKindDef ClassicKind = new("classic", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["task", "feature", "bug"], ClassicStatuses, ClassicTransitions()),
	]);

	// WORK reuses the EXISTING status vocabulary (Pending/InProgress/Done/Blocked/
	// Cancelled) + Review, so live boards and the MCP/UI contract don't break.
	// feature/bug/chore share ONE state machine; the linkConstraints say a NEW feature or
	// bug must link a spec node (task_spec = specRef) — `chore` is absent by design: the
	// home for below-spec engineering hygiene (test fixes, flakes, refactorings) that has
	// no requirement to link. Quick-add is rejected: a work node needs a specRef at birth
	// the bare form can't supply.
	//
	// No `Deferred` status (work-preset-drop-deferred, 2026-07): the maintainer decided a
	// kanban column for "parked, come back later" wasn't worth the extra status — Pending
	// already covers "not started yet" and a card that stalls stays Pending or moves to
	// Blocked. Dropping it from THIS preset does not, by itself, remove it from a document
	// already materialized into an instance's stored RULES (or the project's utility-layer
	// document) before this change (RenderBuiltinTemplate copies a preset kind verbatim at
	// creation time) —
	// WorkDeferredStatusMigrator (PetBox.Tasks.Data) is the one-time startup migration that
	// strips it (status + referencing transitions) from any such stored document.
	// The quartet's ONE blocking gate (spec methodology-blocks-gate-data): the single source of
	// truth this file's own Effects declarations below reference (OnlyFrom/Set/On), rather than
	// repeating "Blocked"/"InProgress" as independent literals that could drift from BlocksGate
	// itself — the whole point of the field is a kind's gate status living in exactly one place.
	static readonly MethodologyBlocksGateDef WorkBlocksGate = new("Blocked", "InProgress");

	static readonly MethodologyKindDef WorkKind = new("work", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["feature", "bug", "chore"],
			[
				new("Pending", "Pending", StatusKind.Open),
				new("InProgress", "In progress", StatusKind.Open),
				new("Review", "Review", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
				new("Blocked", "Blocked", StatusKind.Open),
				new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
			],
			[
				new("Pending", "InProgress"),
				new("InProgress", "Review"),
				new("Review", "InProgress"),                       // reject back
				new("Review", "Done", RequiresApproval: true),     // approve gate
				new("InProgress", "Blocked"),
				new("Blocked", "InProgress"),
				new("Pending", "Cancelled"),
				new("InProgress", "Cancelled"),
				new("Review", "Cancelled"),
			]),
	])
	{
		// Schema v2: the link target is DATA — a specRef must point at a spec-kind node
		// (the guard the service used to hardcode as ValidateSpecRefsAsync).
		LinkConstraints =
		[
			new MethodologyLinkConstraintDef("feature", "task_spec") { TargetKind = "spec" },
			new MethodologyLinkConstraintDef("bug", "task_spec") { TargetKind = "spec" },
		],
		// Schema v2: the FSM effects are DATA (executed by RunTransitionEffectsAsync) —
		// the automation the service used to hardcode as RunDoneEffectsAsync:
		//   - a work node entering Done closes intake issues that spawned it (issue_task
		//     edges point issue -> task, i.e. INCOMING on the work node);
		//   - a work node entering Done releases nodes it was blocking (blocks edges point
		//     blocker -> blocked, i.e. OUTGOING), gate.Status -> gate.ReleaseTo. The `blocks`
		//     kind is a builtin GATING relation: the executor consumes the traversed edge and
		//     applies the effect only when no other active blocker remains.
		// NOT the manual-leave-Blocked unblock (TaskTransitionEffects/TasksService.
		// CloseBlocksOnLeaveAsync) — deliberately kept OUT of this list. MethodologyRuntime.
		// Effects(kindSlug) resolves WHOLE-OBJECT, not field-by-field like BlocksGate/Singleton/
		// DefaultView just below: a real quartet-provisioned project materialized `work` as a
		// DEFINED kind (RenderPresetDefinition, at instance-creation time) carrying its OWN
		// stored Effects list — exactly these two entries, frozen before this field existed. An
		// onLeave entry added HERE would never reach that already-materialized project; only a
		// bare, never-provisioned preset board would see it. Adding it anyway would be the exact
		// DefaultView/Singleton field-materialization trap one level up: this file's own doc
		// comments on Singleton/DefaultView warn against it, and this Effects list is the one
		// place in this class where a whole-object resolver still means "silently invisible on
		// every real project" for anything added here (caught in review before it shipped —
		// methodology-blocks-gate-data). CloseBlocksOnLeaveAsync stays an imperative method
		// instead, reading BlocksGate(kindSlug).Status (field-merged, safe) rather than hardcoding
		// "Blocked".
		Effects =
		[
			new MethodologyTransitionEffectDef(On: "Done", Link: "issue_task", Direction: "incoming", Set: "done"),
			new MethodologyTransitionEffectDef(On: "Done", Link: "blocks", Direction: "outgoing", Set: WorkBlocksGate.ReleaseTo, OnlyFrom: WorkBlocksGate.Status),
		],
		// primitives-enum-residual: work→spec auto-wire is DATA (executed by AutoWireSpecAsync).
		AutoWireFrom = "spec",
		// methodology-default-view-field: work opens in kanban (stage columns) by default.
		// The renderer isn't shipped yet (board-view-mode-framework) — until it is,
		// BoardViewModeRegistry.Resolve degrades this to Tree, so the board still works.
		DefaultView = BoardViewModeNames.Kanban,
		// methodology-kind-singleton: work is a process-role kind, one open board per instance.
		Singleton = true,
		// methodology-blocks-gate-data: work is the quartet's one gated kind — "a Blocked task
		// must name a blocker" is a STATE invariant (GuardEngine.RequireBlockers), not a
		// transition gate.
		BlocksGate = WorkBlocksGate,
	};

	// A spec node is born `defined` (a worked-out requirement) and can only retire to
	// `deprecated` when the requirement loses meaning. There is no draft/in-flux status —
	// undefined thinking lives in an Idea, not the spec tree. Quick-add is rejected: a
	// spec write needs an accepted-idea ideaRef the bare form can't supply.
	static readonly MethodologyKindDef SpecKind = new("spec", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["spec"],
			[
				new("defined", "Defined", StatusKind.Open),
				new("deprecated", "Deprecated", StatusKind.TerminalCancel),
			],
			[
				new("defined", "deprecated"),
			]),
	])
	{
		// Schema v2: spec-write-needs-accepted-idea as DATA (was the hardcoded
		// RequireAcceptedIdeaForSpecAsync): the ideaRef must point at an ideas-kind node in
		// `accepted`. idea_spec is a PROVENANCE link — the constraint binds EVERY write of
		// the type, not just creation (each spec change names the idea that authorizes it).
		LinkConstraints =
		[
			new MethodologyLinkConstraintDef("spec", "idea_spec") { TargetKind = "ideas", TargetStatuses = ["accepted"] },
		],
		// primitives-enum-residual: delivery type roles are DATA (feature drives progress;
		// open bug → done_with_defects). Computed by TasksService.ComputeSpecDeliveryAsync.
		Delivery = new MethodologyDeliveryDef(["feature"], ["bug"], "task_spec"),
		// methodology-default-view-field: spec opens in outline (heading hierarchy) by
		// default. Renderer not shipped yet — degrades to Tree until it is.
		DefaultView = BoardViewModeNames.Outline,
		// board-view-mode-framework: a spec node's body is one short normative line —
		// cheap to fetch and read inline, so the outline view expands it in place rather
		// than sending the reader to the node page.
		OutlineReveal = OutlineRevealModeNames.InlineLazy,
		// methodology-kind-singleton: spec is a process-role kind, one open board per instance.
		Singleton = true,
	};

	// Mirrors the work gate: an idea reaches `review` (agent ceiling), the maintainer
	// approves `review → accepted`. Entering `review` requires an artifact:spec_plan
	// comment — the transition carries the precondition as DATA and
	// RequirePreconditionArtifactsAsync enforces it (the engine stays pure).
	static readonly MethodologyKindDef IdeasKind = new("ideas", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["idea"],
			[
				new("raw", "Raw", StatusKind.Open),
				new("exploring", "Exploring", StatusKind.Open),
				new("review", "Review", StatusKind.Open),
				new("deferred", "Deferred", StatusKind.Open),
				new("accepted", "Accepted", StatusKind.TerminalOk),
				new("rejected", "Rejected", StatusKind.TerminalCancel),
			],
			[
				new("raw", "exploring"),
				new("exploring", "review", PreconditionArtifact: "spec_plan"),
				new("review", "accepted", RequiresApproval: true), // approve gate (maintainer)
				new("review", "exploring"),                        // reject back for more thinking
				new("review", "rejected", RequiresReason: true),
				new("exploring", "rejected", RequiresReason: true),
				new("exploring", "deferred"),
				new("deferred", "exploring"),
			]),
	])
	{
		// methodology-default-view-field: ideas opens in tree — same as the builtin
		// fallback, stated explicitly so the quartet's four kinds are uniformly declared.
		DefaultView = BoardViewModeNames.Tree,
		// methodology-kind-singleton: ideas is a process-role kind, one open board per instance.
		Singleton = true,
	};

	static readonly MethodologyKindDef IntakeKind = new("intake", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["issue"],
			[
				new("reported", "Reported", StatusKind.Open),
				new("triage", "Triage", StatusKind.Open),
				new("confirmed", "Confirmed", StatusKind.Open),
				new("duplicate", "Duplicate", StatusKind.TerminalCancel),
				new("wontfix", "Won't fix", StatusKind.TerminalCancel),
				new("done", "Done", StatusKind.TerminalOk),
			],
			[
				new("reported", "triage"),
				new("triage", "confirmed"),
				new("triage", "duplicate", RequiresReason: true),
				new("triage", "wontfix", RequiresReason: true),
				new("confirmed", "done", RequiresApproval: true),
			]),
	])
	{
		// methodology-default-view-field: intake opens in table (scannable inbox rows) by
		// default. Renderer not shipped yet — degrades to Tree until it is.
		DefaultView = BoardViewModeNames.Table,
		// methodology-kind-singleton: intake is a process-role kind, one open board per instance.
		Singleton = true,
	};

	// OBSERVATION (work observation-kind-and-dedup): the system `observations` board's kind.
	// A captured signal — from the extractor or a manual write — starts `seen`; `promoted`
	// once the (separate, not-yet-built) promote tool turns it into edges/spec/work;
	// `fixed` (TerminalOk) once the underlying issue is actually gone, kept DISTINCT from
	// `declined` (TerminalCancel, "not worth acting on") — a regression detector needs to
	// tell "we fixed it and it came back" from "we decided it never mattered", which a
	// three-value model can't express. No FSM engine, no traits: this is a single flat
	// state machine like every other preset above, just four statuses and four trivial
	// edges. No LinkConstraints/Effects/AutoWireFrom/Delivery — the promote tool (a
	// neighboring card) owns turning a promoted observation into edges; this preset only
	// carries the shape of the status lifecycle. Recurrence accumulation lives OUTSIDE this
	// FSM entirely — a service-layer dedup guard (ObservationDedupService, PetBox.Web) that
	// intercepts a CREATE before it reaches here, so a repeat sighting never becomes a
	// second node with its own trivial "seen" status to track.
	static readonly WorkflowStatus[] ObservationStatuses =
	[
		new("seen", "Seen", StatusKind.Open),
		new("promoted", "Promoted", StatusKind.Open),
		new("fixed", "Fixed", StatusKind.TerminalOk),
		new("declined", "Declined", StatusKind.TerminalCancel),
	];

	static readonly MethodologyKindDef ObservationKind = new("observation", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["observation"], ObservationStatuses,
		[
			new("seen", "promoted"),
			new("seen", "declined", RequiresReason: true),
			new("promoted", "fixed"),
			new("promoted", "declined", RequiresReason: true),
		]),
	]);

	public static MethodologyKindDef KindDef(BoardKind kind) => kind switch
	{
		BoardKind.Spec => SpecKind,
		BoardKind.Ideas => IdeasKind,
		BoardKind.Intake => IntakeKind,
		BoardKind.Observation => ObservationKind,
		BoardKind.Work => WorkKind,
		BoardKind.Classic => ClassicKind,
		_ => SimpleKind,
	};

	// ---- preset tag axes ----

	// The builtin controlled tag namespaces (spec-flat-tags): small and orthogonal.
	public static readonly IReadOnlyList<MethodologyTagAxisDef> BuiltinAxes =
	[
		new MethodologyTagAxisDef("area"),
		new MethodologyTagAxisDef("concern"),
	];

	// The quartet kinds enforce the builtin axes; `simple` and `classic` declare NONE — so
	// the one axes-emptiness rule (no axes = free-form tags) reproduces "methodology boards
	// enforce, simple/classic don't" without a second mechanism.
	public static IReadOnlyList<MethodologyTagAxisDef> TagAxes(BoardKind kind) =>
		kind is BoardKind.Simple or BoardKind.Classic ? [] : BuiltinAxes;

	// ---- the quartet's PROCESS relation kinds as DATA (spec methodology-link-kinds-declared) ----
	//
	// idea_spec/task_spec/issue_task used to be builtin string literals in
	// MethodologyRuntime.ProcessRelationKinds; they are now DECLARED relation kinds carried on the
	// quartet definition's LinkKinds, each with its stored-edge Direction. This is the SANCTIONED
	// literal home for the trio (criterion-0): the runtime falls back here for the trio's direction
	// and vocabulary, the validator admits the trio in effects/constraints, and RenderPresetDefinition
	// seeds them into a quartet document. A project may override any of them by declaring its own
	// linkKind with the same slug (declared wins over this preset fallback). Descriptions/labels are
	// carried over from the quartet methodology's original design proposal (§1.1 "expressed").
	public static readonly IReadOnlyList<MethodologyLinkKindDef> QuartetLinkKinds =
	[
		new MethodologyLinkKindDef("idea_spec",
			"Спека реализует принятую идею — провенанс: каждый лист спеки восходит к идее, которую владелец принял.",
			LinkCategory.Process,
			new MethodologyLinkDirectionDef("ideas", "spec", "реализует")),
		new MethodologyLinkKindDef("task_spec",
			"Задача поставляет обещание спеки — feature/bug несут способность/дефект против листа спеки; chore не несёт.",
			LinkCategory.Process,
			new MethodologyLinkDirectionDef("work", "spec", "поставляет")),
		new MethodologyLinkKindDef("issue_task",
			"Задача закрывает интейк-issue — когда работа доходит до Done, входящий issue автозакрывается.",
			LinkCategory.Process,
			new MethodologyLinkDirectionDef("intake", "work", "закрывает")),
	];

	// OBSERVATION PROMOTION (work observation-edges-promote-and-nail): the ONE relation kind
	// linking a promoted observation to the obligation (a work feature/bug/chore, or an ideas
	// node) that addresses it — FromNodeId=observation, ToNodeId=obligation, mirroring
	// issue_task's orientation (the origin signal points at what was produced to answer it).
	// Declared the SAME WAY as the quartet's process trio just above — a builtin,
	// project-independent fallback (MethodologyRuntime concatenates it into
	// KnownRelationKinds/LinkKind/EffectiveLinkKinds unconditionally) rather than a
	// methodology-instance-declared linkKind, because the system `observations` board lives in
	// the project's $utility world, outside any methodology instance — this edge must resolve
	// regardless of which (or whether any) instance is active. ToKind is null (unconstrained):
	// the obligation may land on either a `work` or an `ideas` board, so the direction pins
	// only the observation end.
	public const string ObservationObligationLinkKind = "observation_obligation";

	public static readonly IReadOnlyList<MethodologyLinkKindDef> ObservationLinkKinds =
	[
		new MethodologyLinkKindDef(ObservationObligationLinkKind,
			"Наблюдение промоутится в обязательство (work-фичу/баг/chore или ideas-узел), которое его адресует — наблюдение остаётся адресуемым узлом доски, а не исчезает.",
			LinkCategory.Process,
			new MethodologyLinkDirectionDef("observation", null, "адресуется через")),
	];

	// ---- resolution helpers over the preset data ----

	// Board kinds where the bare board quick-add form is valid — preset data now, same
	// policy as always: only Spec and Work reject it (their nodes need a LINK at birth).
	public static bool QuickAddAllowed(BoardKind kind) => KindDef(kind).QuickAddAllowed;

	// The type an untyped quick-add resolves to: the first type of the first block —
	// declaration order is meaningful, like Statuses[0] = initial. Produces the historical
	// defaults: ideas→idea, spec→spec, intake→issue, simple→task.
	public static string DefaultType(BoardKind kind) => KindDef(kind).Workflows[0].Types[0];

	// The workflow for a (kind, type). Work is STRICT: type selects the workflow and an
	// unknown/empty type yields null (the "type required" contract). A SINGLE-BLOCK kind
	// hosts one state machine — type is a label, not a branch, so an EMPTY type resolves
	// the one FSM (the historical catalog semantics: an untyped node on a spec/ideas/intake/
	// simple board still resolves its kind's workflow). Any kind's type vocabulary is
	// enforced HERE: an out-of-vocab type always yields null naming the valid ones — strict
	// like the declared-kind path (MethodologyRuntime.For), no lazy fallback. A MULTI-BLOCK
	// non-Work kind (none among the presets today; the resolution stays preset-agnostic) is
	// lenient only for the EMPTY type (→ the first block's default type); a non-empty type
	// must select its block — an unknown type is ambiguous across blocks, so it yields null
	// like Work does.
	public static Workflow? For(BoardKind kind, string? type)
	{
		var def = KindDef(kind);
		if (string.IsNullOrEmpty(type))
			return kind == BoardKind.Work ? null : def.Workflows[0].ToWorkflow(def.Workflows[0].Types[0]);
		var label = type.ToLowerInvariant();
		var block = def.Workflows.FirstOrDefault(b => b.Types.Contains(label, StringComparer.OrdinalIgnoreCase));
		return block?.ToWorkflow(label);
	}

	// All workflows hosted by a kind, one per type slug (status-filter validation).
	public static IReadOnlyList<Workflow> Types(BoardKind kind) =>
		KindDef(kind).Workflows.SelectMany(b => b.Types.Select(t => b.ToWorkflow(t))).ToList();

	// All workflow BLOCKS of a kind (the tasks_workflow discovery shape): the preset data
	// is already grouped by shared FSM (feature=bug=chore is ONE block; simple's block
	// carries its whole type vocabulary).
	public static IReadOnlyList<WorkflowBlock> Blocks(BoardKind kind) =>
		KindDef(kind).Workflows.Select(b => new WorkflowBlock(b.Types, b.ToWorkflow(b.Types[0]))).ToList();

	// Valid type slugs for a kind (for error messages).
	public static string ValidTypes(BoardKind kind) =>
		string.Join("|", KindDef(kind).Workflows.SelectMany(b => b.Types));

	static readonly BoardKind[] AllKinds = [BoardKind.Simple, BoardKind.Classic, BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

	// StatusKind for a status slug across ALL presets (case-insensitive), or null if
	// the slug isn't in any preset workflow (e.g. a legacy free-board status pre-migration).
	public static StatusKind? KindOfSlug(string slug)
	{
		foreach (var k in AllKinds)
			foreach (var block in KindDef(k).Workflows)
				foreach (var s in block.Statuses)
					if (string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase))
						return s.Kind;
		return null;
	}

	// The declared human display Name for a status slug across ALL presets (case-insensitive),
	// or null if the slug isn't in any preset workflow. Presentation only — this is the label
	// the badge/select show (e.g. `InProgress` → "In progress", `defined` → "Defined"); the
	// stored slug is unchanged.
	public static string? NameOfSlug(string slug)
	{
		foreach (var k in AllKinds)
			foreach (var block in KindDef(k).Workflows)
				foreach (var s in block.Statuses)
					if (string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase))
						return s.Name;
		return null;
	}

	// ---- provisioning presets (instance provisioning + copy-as-document) ----

	// A named PROVISIONING PRESET: the board kinds ONE provisioning act creates as a unit —
	// `tasks_methodology_create` with source `builtin` and this slug as `sourceKey`, or the
	// admin enable UI on the same path (it prefers that door and falls back to the
	// EnableMethodologyAsync compat layer for an existing instance) — plus the human-facing
	// metadata that UI renders. The point of the registry is that a new preset (e.g. a leaner
	// "classic" pipeline) is added here as PURE DATA — no surface (service / MCP tool / admin
	// UI) changes, they all read this list.
	public sealed record MethodologyProvisioningPreset(
		string Slug, string DisplayName, string Description, IReadOnlyList<BoardKind> Kinds);

	// The slug the callers that MAY omit one fall back to: the admin enable UI's empty
	// <select> and EnableMethodologyAsync's default argument. It is NOT a default for
	// tasks_methodology_create — an instance is born from an EXPLICIT source, there is no
	// silent quartet — and it is no longer the only preset: `classic` is registered right below.
	public const string DefaultProvisioningPreset = "quartet";

	// The provisioning preset registry: the quartet (intake→ideas→spec→work, enabled since
	// the methodology shipped) and `classic` (one standalone GitHub/Jira/Linear-level board).
	public static readonly IReadOnlyList<MethodologyProvisioningPreset> ProvisioningPresets =
	[
		new("quartet", "Methodology quartet",
			"The intake → ideas → spec → work pipeline: four singleton boards, work auto-wired to spec.",
			[BoardKind.Intake, BoardKind.Ideas, BoardKind.Spec, BoardKind.Work]),
		new("classic", "Classic",
			"A single-kind status model at the level of the GitHub/Jira/Linear defaults: one classic board (task|feature|bug), free transitions among open statuses, free-form tags.",
			[BoardKind.Classic]),
	];

	// Resolve a preset slug (case-insensitive; null/blank = the default). An unknown slug is a
	// clear error listing the available slugs — the same posture as an unknown board kind.
	public static MethodologyProvisioningPreset ResolveProvisioningPreset(string? slug)
	{
		var s = (slug ?? DefaultProvisioningPreset).Trim().ToLowerInvariant();
		if (s.Length == 0) s = DefaultProvisioningPreset;
		return ProvisioningPresets.FirstOrDefault(p => string.Equals(p.Slug, s, StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException(
				$"unknown methodology preset '{slug}' — available presets: {string.Join("|", ProvisioningPresets.Select(p => p.Slug))}");
	}

	// Render a provisioning preset as a MethodologyDefinition DOCUMENT — the same shapes the
	// presets already build (one KindDef per board kind + the builtin tag axes) — so a user can
	// copy it as a starting point and edit it through tasks_methodology_rules_upsert (a live
	// instance's rules) or tasks_methodology_template_upsert (an inert template). The document
	// passes MethodologyDefinitionValidator (the preset slug becomes the document's `name` — a
	// nickname, NOT an address: an instance is addressed by its `key`; every kind slug, status
	// and transition comes straight from the preset data). Read-only: the returned
	// definition is a document, NOT a live instance — nothing is provisioned until
	// tasks_methodology_create names it as a source. The data-born semantics (link
	// constraints incl. targets — the ideaRef/specRef guards — transition effects — intake
	// auto-close, blocks auto-unblock — auto-wire work→spec, delivery type roles, the
	// one-open-board `singleton` flag, the blocker gate and the default view) all DO travel
	// with the copy: they are fields on the copied KindDef. What stays outside the document is
	// only what the BoardKind enum still keys (see ParseKind above) — the preset fallback for
	// an undeclared kind and the quartet compat surface.
	public static MethodologyDefinition RenderPresetDefinition(string? slug)
	{
		var preset = ResolveProvisioningPreset(slug);
		return new MethodologyDefinition(preset.Slug, preset.Kinds.Select(KindDef).ToList())
		{
			// The axes of the preset's OWN kinds (quartet → the builtin area/concern pair;
			// classic → none = free-form), so the copy keeps the preset's tag posture.
			TagAxes = preset.Kinds.SelectMany(TagAxes).DistinctBy(a => a.Namespace).ToList(),
			// The quartet's process relation kinds as DATA (methodology-link-kinds-declared): the
			// trio (idea_spec/task_spec/issue_task with direction) is materialized into the stored
			// document only when the preset carries the pipeline kinds those directions reference —
			// classic/simple carry no process trio.
			LinkKinds = preset.Kinds.Contains(BoardKind.Work) ? QuartetLinkKinds : [],
		};
	}

	// Builtin TEMPLATE keys (methodology-template-storage): the documents readable through
	// tasks_methodology_template_get/list with source="builtin". Superset of provisioning
	// presets — adds `simple` (a single-kind free-lifecycle board; not a provisioning unit
	// because a bare `tasks_board_create` already gives an empty board the `simple` kind).
	public static readonly IReadOnlyList<string> BuiltinTemplateKeys = ["quartet", "classic", "simple"];

	// Render a builtin template key as a MethodologyDefinition. quartet|classic go through
	// RenderPresetDefinition; simple is the standalone SimpleKind document (no tag axes =
	// free-form). Unknown key is a clear error listing the available keys.
	public static MethodologyDefinition RenderBuiltinTemplate(string? slug)
	{
		var s = (slug ?? "").Trim().ToLowerInvariant();
		if (s is "quartet" or "classic")
			return RenderPresetDefinition(s);
		if (s == "simple")
			return new MethodologyDefinition("simple", [SimpleKind]);
		throw new ArgumentException(
			$"unknown methodology builtin template '{slug}' — available: {string.Join("|", BuiltinTemplateKeys)}");
	}
}
