namespace PetBox.Tasks.Workflow;

// The built-in processes — the methodology quartet (intake|ideas|spec|work) and the
// lightweight `simple` kind — expressed as PRESET METHODOLOGY DEFINITIONS: the same
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
	// The `preset` name a definition-less project reports (tasks.methodology_def_get).
	public const string Name = "builtin-presets";

	// Kind slug → process-role enum. The enum is the key for semantics that are NOT yet
	// primitives (quartet singleton rule, spec delivery roll-up, FSM effects, ideaRef/specRef
	// board-kind checks, UI kind rendering). Unknown slugs — including the legacy `free`
	// (pre-M029 rows) — read as Simple, exactly as they always did.
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

	// WORK reuses the EXISTING status vocabulary (Pending/InProgress/Done/Blocked/
	// Deferred/Cancelled) + Review, so live boards and the MCP/UI contract don't break.
	// feature/bug/chore share ONE state machine; the linkConstraints say a NEW feature or
	// bug must link a spec node (task_spec = specRef) — `chore` is absent by design: the
	// home for below-spec engineering hygiene (test fixes, flakes, refactorings) that has
	// no requirement to link. Quick-add is rejected: a work node needs a specRef at birth
	// the bare form can't supply.
	static readonly MethodologyKindDef WorkKind = new("work", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["feature", "bug", "chore"],
			[
				new("Pending", "Pending", StatusKind.Open),
				new("InProgress", "In progress", StatusKind.Open),
				new("Review", "Review", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
				new("Blocked", "Blocked", StatusKind.Open),
				new("Deferred", "Deferred", StatusKind.Open),
				new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
			],
			[
				new("Pending", "InProgress"),
				new("InProgress", "Review"),
				new("Review", "InProgress"),                       // reject back
				new("Review", "Done", RequiresApproval: true),     // approve gate
				new("InProgress", "Blocked"),
				new("Blocked", "InProgress"),
				new("Pending", "Deferred"),
				new("Deferred", "Pending"),
				new("Pending", "Cancelled"),
				new("InProgress", "Cancelled"),
				new("Review", "Cancelled"),
			]),
	])
	{
		LinkConstraints =
		[
			new MethodologyLinkConstraintDef("feature", "task_spec"),
			new MethodologyLinkConstraintDef("bug", "task_spec"),
		],
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
	]);

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
	]);

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
	]);

	public static MethodologyKindDef KindDef(BoardKind kind) => kind switch
	{
		BoardKind.Spec => SpecKind,
		BoardKind.Ideas => IdeasKind,
		BoardKind.Intake => IntakeKind,
		BoardKind.Work => WorkKind,
		_ => SimpleKind,
	};

	// ---- preset tag axes ----

	// The builtin controlled tag namespaces (spec-flat-tags): small and orthogonal.
	public static readonly IReadOnlyList<MethodologyTagAxisDef> BuiltinAxes =
	[
		new MethodologyTagAxisDef("area"),
		new MethodologyTagAxisDef("concern"),
	];

	// The quartet kinds enforce the builtin axes; `simple` declares NONE — so the one
	// axes-emptiness rule (no axes = free-form tags) reproduces "methodology boards
	// enforce, simple doesn't" without a second mechanism.
	public static IReadOnlyList<MethodologyTagAxisDef> TagAxes(BoardKind kind) =>
		kind == BoardKind.Simple ? [] : BuiltinAxes;

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
	// unknown/empty type yields null (the "type required" contract). Every other preset
	// kind hosts ONE state machine — type is a label, not a branch, so For ignores it
	// (the historical catalog semantics: an untyped or oddly-typed node on a spec/ideas/
	// intake/simple board still resolves its kind's workflow; simple's type VOCABULARY is
	// enforced separately at the write door).
	public static Workflow? For(BoardKind kind, string? type)
	{
		var def = KindDef(kind);
		if (kind != BoardKind.Work)
		{
			var block = def.Workflows[0];
			var label = string.IsNullOrEmpty(type) ? block.Types[0] : type.ToLowerInvariant();
			return block.ToWorkflow(label);
		}
		if (string.IsNullOrEmpty(type)) return null;
		var match = def.Workflows.FirstOrDefault(b => b.Types.Contains(type, StringComparer.OrdinalIgnoreCase));
		return match?.ToWorkflow(type.ToLowerInvariant());
	}

	// All workflows hosted by a kind, one per type slug (status-filter validation).
	public static IReadOnlyList<Workflow> Types(BoardKind kind) =>
		KindDef(kind).Workflows.SelectMany(b => b.Types.Select(b.ToWorkflow)).ToList();

	// All workflow BLOCKS of a kind (the tasks.workflow discovery shape): the preset data
	// is already grouped by shared FSM (feature=bug=chore is ONE block; simple's block
	// carries its whole type vocabulary).
	public static IReadOnlyList<WorkflowBlock> Blocks(BoardKind kind) =>
		KindDef(kind).Workflows.Select(b => new WorkflowBlock(b.Types, b.ToWorkflow(b.Types[0]))).ToList();

	// Valid type slugs for a kind (for error messages).
	public static string ValidTypes(BoardKind kind) =>
		string.Join("|", KindDef(kind).Workflows.SelectMany(b => b.Types));

	static readonly BoardKind[] AllKinds = [BoardKind.Simple, BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

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

	// A node is "closed" (hidden under active-only) if its status is terminal in some preset.
	public static bool IsTerminalSlug(string slug) =>
		KindOfSlug(slug) is StatusKind.TerminalOk or StatusKind.TerminalCancel;
}
