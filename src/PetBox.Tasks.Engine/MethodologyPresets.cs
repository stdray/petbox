namespace PetBox.Tasks.Workflow;

// The built-in processes — the methodology quartet (intake|ideas|spec|work) and the
// standalone `simple` and `classic` kinds — expressed as PRESET METHODOLOGY DEFINITIONS: the same
// MethodologyDefinition shapes a project can store, constructed in code (spec
// primitives-preset-quartet). This replaces the hardcoded WorkflowCatalog 1:1, and the
// wave-2 primitives that used to be imperative service code are now preset DATA:
//   - work linkConstraints: feature/bug must carry task_spec (specRef) at creation;
//     chore has NO constraint — that IS the chore exemption, as data;
//   - ideas: exploring→review carries preconditionArtifact "spec_plan" (the idea-review
//     gate, enforced by RequirePreconditionArtifactsAsync like any definition gate);
//   - tag axes: the quartet kinds run on the builtin area/concern axes; simple declares
//     none — axes-emptiness = free-form tags is the ONE rule for every kind.
// MethodologyRuntime falls back here for any kind slug the project's definition does not
// declare, so preset boards behave exactly as before while a definition overrides per kind.
public static class MethodologyPresets
{
	// The `preset` name a definition-less project reports (tasks_methodology_def_get).
	public const string Name = "builtin-presets";

	// Kind slug → process-role enum. The enum is the key for semantics that are NOT yet
	// primitives (quartet singleton rule, ideaRef/specRef board-kind checks, UI kind
	// rendering). Auto-wire and delivery type roles are DATA on KindDef. Unknown slugs —
	// including the legacy `free` (pre-M029 rows) — read as Simple, exactly as they always did.
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
	// three), InReview (Linear's default started status; GitHub models review outside
	// Issues), Done, and the not-delivered pair Cancelled/Duplicate (GitHub close reasons
	// "not planned"/"duplicate", Linear's Canceled/Duplicate). Transitions are FREE among
	// the OPEN statuses (Jira's default workflow allows all transitions; Linear/GitHub
	// don't gate status moves — low ceremony wins); terminals are reached EXPLICITLY, with
	// a reason required only INTO Duplicate (a duplicate without a pointer to the original
	// is useless; Cancelled needs none — GitHub closes "not planned" without a mandatory
	// reason), and a closed node reopens to Todo (the GitHub reopen). No approval gates
	// anywhere, no link constraints, no effects, no checklists, free-form tags — and NO
	// quartet semantics (no singleton rule, no auto-wire), same as `simple`.
	static readonly WorkflowStatus[] ClassicStatuses =
	[
		new("Backlog", "Backlog", StatusKind.Open),
		new("Todo", "Todo", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("InReview", "In review", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
		new("Duplicate", "Duplicate", StatusKind.TerminalCancel),
	];

	// Classic's edge set: all ordered pairs among the OPEN statuses (free movement), every
	// open status may close explicitly (Done and Cancelled ungated; Duplicate demands a
	// reason — the pointer to the original), and each terminal reopens to Todo.
	static List<MethodologyTransitionDef> ClassicTransitions()
	{
		var open = ClassicStatuses.Where(s => s.Kind == StatusKind.Open).Select(s => s.Slug).ToList();
		var edges = new List<MethodologyTransitionDef>();
		foreach (var from in open)
			foreach (var to in open.Where(t => t != from))
				edges.Add(new(from, to));
		foreach (var from in open)
		{
			edges.Add(new(from, "Done"));
			edges.Add(new(from, "Cancelled"));
			edges.Add(new(from, "Duplicate", RequiresReason: true));
		}
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
	// Blocked. Dropping it from THIS preset does not, by itself, remove it from a
	// definition already materialized into a project's stored methodology document before
	// this change (RenderBuiltinTemplate copies a preset kind verbatim at creation time) —
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
		//     applies the effect only when no other active blocker remains;
		//   - methodology-blocks-gate-data: a work node LEAVING gate.Status (Effect.onLeave —
		//     someone manually moves a Blocked task elsewhere, not through its blocker's Done)
		//     closes every INCOMING blocks edge unconditionally, history kept, nobody's status
		//     forced (Set: null — a pure edge-consumption effect). Was TasksService.
		//     CloseBlocksOnLeaveAsync, a bespoke method hardcoded to the literal "Blocked"; now
		//     this declared entry, keyed off WorkBlocksGate like the entry above it.
		Effects =
		[
			new MethodologyTransitionEffectDef(On: "Done", Link: "issue_task", Direction: "incoming", Set: "done"),
			new MethodologyTransitionEffectDef(On: "Done", Link: "blocks", Direction: "outgoing", Set: WorkBlocksGate.ReleaseTo, OnlyFrom: WorkBlocksGate.Status),
			new MethodologyTransitionEffectDef(On: WorkBlocksGate.Status, Link: "blocks", Direction: "incoming", Set: null, OnLeave: true),
		],
		// primitives-enum-residual: work→spec auto-wire is DATA (executed by AutoWireSpecAsync).
		AutoWireSpecFrom = "spec",
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
		Delivery = new MethodologyDeliveryDef(["feature"], ["bug"]),
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

	public static MethodologyKindDef KindDef(BoardKind kind) => kind switch
	{
		BoardKind.Spec => SpecKind,
		BoardKind.Ideas => IdeasKind,
		BoardKind.Intake => IntakeKind,
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

	// ---- resolution helpers over the preset data ----

	// Simple's fixed-but-small type vocabulary. Type does NOT branch the workflow (one
	// lifecycle for all); it's a filter/badge label. Empty type defaults to `task`.
	public static IReadOnlyList<string> SimpleTypes => SimpleKind.Workflows[0].Types;

	// Board kinds where the bare board quick-add form is valid — preset data now, same
	// policy as always: only Spec and Work reject it (their nodes need a LINK at birth).
	public static bool QuickAddAllowed(BoardKind kind) => KindDef(kind).QuickAddAllowed;

	// The type an untyped quick-add resolves to: the first type of the first block —
	// declaration order is meaningful, like Statuses[0] = initial. Produces the historical
	// defaults: ideas→idea, spec→spec, intake→issue, simple→task.
	public static string DefaultType(BoardKind kind) => KindDef(kind).Workflows[0].Types[0];

	// The workflow for a (kind, type). Work is STRICT: type selects the workflow and an
	// unknown/empty type yields null (the "type required" contract). A SINGLE-BLOCK kind
	// hosts one state machine — type is a label, not a branch, so any/empty type resolves
	// the one FSM (the historical catalog semantics: an untyped or oddly-typed node on a
	// spec/ideas/intake/simple board still resolves its kind's workflow; simple's type
	// VOCABULARY is enforced separately at the write door). Classic is single-block too,
	// but its type vocabulary is enforced HERE: empty resolves the default type, an
	// out-of-vocab type yields null naming the valid ones — strict like Work, without a
	// second write-door case. A MULTI-BLOCK non-Work kind (none among the presets today;
	// the resolution stays preset-agnostic) is lenient only for the EMPTY type (→ the
	// first block's default type); a non-empty type must select its block — an unknown
	// type is ambiguous across blocks, so it yields null like Work does.
	public static Workflow? For(BoardKind kind, string? type)
	{
		var def = KindDef(kind);
		if (string.IsNullOrEmpty(type))
			return kind == BoardKind.Work ? null : def.Workflows[0].ToWorkflow(def.Workflows[0].Types[0]);
		var label = type.ToLowerInvariant();
		var block = def.Workflows.FirstOrDefault(b => b.Types.Contains(label, StringComparer.OrdinalIgnoreCase));
		if (block is not null) return block.ToWorkflow(label);
		return kind is not BoardKind.Work and not BoardKind.Classic && def.Workflows.Count == 1
			? def.Workflows[0].ToWorkflow(label) : null;
	}

	// All workflows hosted by a kind, one per type slug (status-filter validation).
	public static IReadOnlyList<Workflow> Types(BoardKind kind) =>
		KindDef(kind).Workflows.SelectMany(b => b.Types.Select(b.ToWorkflow)).ToList();

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

	// A node is "closed" (hidden under active-only) if its status is terminal in some preset.
	public static bool IsTerminalSlug(string slug) =>
		KindOfSlug(slug) is StatusKind.TerminalOk or StatusKind.TerminalCancel;

	// ---- provisioning presets (methodology enable + copy-as-definition) ----

	// A named PROVISIONING PRESET: the board kinds `tasks_methodology_enable` creates as one
	// unit, plus human-facing metadata for the enable UI. The point of the registry is that a
	// new preset (e.g. a leaner "classic" pipeline) is added here as PURE DATA — no surface
	// (service / MCP tool / admin UI) changes, they all read this list.
	public sealed record MethodologyProvisioningPreset(
		string Slug, string DisplayName, string Description, IReadOnlyList<BoardKind> Kinds);

	// The slug enable defaults to (and the historical, only preset today).
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
	// copy it as a starting point and edit it through tasks_methodology_def_upsert. The document
	// passes MethodologyDefinitionValidator (the preset slug is the definition name; every kind
	// slug, status and transition come straight from the preset data). Read-only: the returned
	// definition is a template, NOT an installed methodology. The data-born semantics (link
	// constraints incl. targets — the ideaRef/specRef guards — transition effects — intake
	// auto-close, blocks auto-unblock — auto-wire work→spec, and delivery type roles) DO
	// travel with the copy; only the enum-keyed extras (quartet singleton rule) stay
	// preset-only.
	public static MethodologyDefinition RenderPresetDefinition(string? slug)
	{
		var preset = ResolveProvisioningPreset(slug);
		return new MethodologyDefinition(preset.Slug, preset.Kinds.Select(KindDef).ToList())
		{
			// The axes of the preset's OWN kinds (quartet → the builtin area/concern pair;
			// classic → none = free-form), so the copy keeps the preset's tag posture.
			TagAxes = preset.Kinds.SelectMany(TagAxes).DistinctBy(a => a.Namespace).ToList(),
		};
	}

	// Builtin TEMPLATE keys (methodology-template-storage): the documents readable through
	// tasks_methodology_template_get/list with source="builtin". Superset of provisioning
	// presets — adds `simple` (a single-kind free-lifecycle board; not a provisioning unit
	// because enable already defaults empty boards to simple via board_create).
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
