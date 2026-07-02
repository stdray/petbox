namespace PetBox.Tasks.Workflow;

// Code-first preset catalog: board kind -> task type(s) -> state machine.
// First iteration seeds these in code (no per-project storage / editor yet).
// The engine reads them as data, so a future DSL/UI just supplies the same shapes.
public static class WorkflowCatalog
{
	public static BoardKind ParseKind(string? kind) =>
		Enum.TryParse<BoardKind>(kind, ignoreCase: true, out var k) ? k : BoardKind.Simple;

	// SIMPLE preset (formerly `free`; interim dogfood, not a PetBox promise — the built-in
	// WorkflowCatalog is a replaceable preset, see spec methodology-from-primitives / idea
	// user-defined-methodology-engine). A minimal lifecycle with FREE transitions: Todo→InProgress→
	// Done(+Cancelled), Blocked optional. Transitions are all-pairs (any valid status → any), so the
	// generic engine yields free transitions while still rejecting an out-of-vocab status. Type is a
	// label only (one workflow for all simple types) — see SimpleTypes.
	static readonly WorkflowStatus[] SimpleStatuses =
	[
		new("Todo", "Todo", StatusKind.Open),
		new("InProgress", "In progress", StatusKind.Open),
		new("Blocked", "Blocked", StatusKind.Open),
		new("Done", "Done", StatusKind.TerminalOk),
		new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
	];

	static readonly Workflow Simple = new("simple", SimpleStatuses, AllPairs(SimpleStatuses));

	// Simple's fixed-but-small type vocabulary. Type does NOT branch the workflow (one lifecycle for
	// all); it's a filter/badge label. Empty type defaults to `task` (see ApplyWorkflow).
	public static readonly string[] SimpleTypes = ["task", "bug", "feature", "chore", "issue"];

	// Every ordered (from→to) pair with from≠to — models "free transitions" for a kind.
	static List<WorkflowTransition> AllPairs(IReadOnlyList<WorkflowStatus> statuses) =>
		(from a in statuses
		 from b in statuses
		 where !string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase)
		 select new WorkflowTransition(a.Slug, b.Slug)).ToList();

	// WORK reuses the EXISTING status vocabulary (Pending/InProgress/Done/Blocked/
	// Deferred/Cancelled) + Review, so live boards and the MCP/UI contract don't break.
	static Workflow Work(string type) => new(type,
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
		]);

	// A spec node is born `defined` (a worked-out requirement) and can only retire to
	// `deprecated` when the requirement loses meaning. There is no draft/in-flux status —
	// undefined thinking lives in an Idea, not the spec tree.
	static readonly Workflow Spec = new("spec",
		[
			new("defined", "Defined", StatusKind.Open),
			new("deprecated", "Deprecated", StatusKind.TerminalCancel),
		],
		[
			new("defined", "deprecated"),
		]);

	// Mirrors the work gate: an idea reaches `review` (agent ceiling), the maintainer
	// approves `review → accepted`. Entering `review` requires an artifact:spec_plan
	// comment — enforced in TasksService (the engine stays pure), not as a transition flag.
	static readonly Workflow Idea = new("idea",
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
			new("exploring", "review"),                        // needs an artifact:spec_plan (guarded in the service)
			new("review", "accepted", RequiresApproval: true), // approve gate (maintainer)
			new("review", "exploring"),                        // reject back for more thinking
			new("review", "rejected", RequiresReason: true),
			new("exploring", "rejected", RequiresReason: true),
			new("exploring", "deferred"),
			new("deferred", "exploring"),
		]);

	static readonly Workflow Issue = new("issue",
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
		]);

	// `chore` shares the exact feature/bug FSM but is exempt from the spec-link guard
	// (RequireSpecLinks) — the home for below-spec engineering hygiene (test fixes,
	// flakes, refactorings) that has no requirement to link.
	static readonly string[] WorkTypes = ["feature", "bug", "chore"];

	// Board kinds where the bare board quick-add form is valid. Quick-add writes a node
	// straight in, so it's only rejected where a node needs a LINK at birth that the bare
	// form can't supply: Spec (an accepted-idea ideaRef) and Work (a specRef). Free, Ideas
	// (born `raw`) and Intake (born `reported`) carry no creation-time gate — their gates
	// live on later transitions — so the form stays. Single declarative knob: the page hides
	// the form and rejects the POST off this, and the tests read it (no per-kind
	// duplication). Flip a kind in here to change where quick-add is offered.
	public static bool QuickAddAllowed(BoardKind kind) => kind is not (BoardKind.Spec or BoardKind.Work);

	// The workflow for a (kind, type). Null = no workflow only for an unknown Work type
	// (a "type required" error). Simple now has a real preset workflow (free transitions).
	public static Workflow? For(BoardKind kind, string? type) => kind switch
	{
		BoardKind.Simple => Simple, // one lifecycle for all simple types (type is a label, not a branch)
		BoardKind.Spec => Spec,
		BoardKind.Ideas => Idea,
		BoardKind.Intake => Issue,
		BoardKind.Work => WorkTypes.Contains((type ?? "").ToLowerInvariant()) ? Work(type!.ToLowerInvariant()) : null,
		_ => null,
	};

	// All workflows hosted by a board kind (for the tasks.workflow discovery tool).
	public static IReadOnlyList<Workflow> Types(BoardKind kind) => kind switch
	{
		BoardKind.Simple => [Simple],
		BoardKind.Spec => [Spec],
		BoardKind.Ideas => [Idea],
		BoardKind.Intake => [Issue],
		BoardKind.Work => [Work("feature"), Work("bug"), Work("chore")],
		_ => [],
	};

	static readonly BoardKind[] AllKinds = [BoardKind.Simple, BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

	// StatusKind for a status slug across ALL presets (case-insensitive), or null if
	// the slug isn't in any workflow (e.g. a legacy free-board status pre-migration).
	public static StatusKind? KindOfSlug(string slug)
	{
		foreach (var k in AllKinds)
			foreach (var wf in Types(k))
				if (wf.Status(slug) is { } s)
					return s.Kind;
		return null;
	}

	// A node is "closed" (hidden under active-only) if its status is terminal in some
	// preset. Free-board slugs match the legacy Done/Cancelled by name.
	public static bool IsTerminalSlug(string slug) =>
		KindOfSlug(slug) is StatusKind.TerminalOk or StatusKind.TerminalCancel;

	// Valid type slugs for a kind (for error messages).
	public static string ValidTypes(BoardKind kind) => kind switch
	{
		BoardKind.Simple => string.Join("|", SimpleTypes),
		BoardKind.Work => string.Join("|", WorkTypes),
		BoardKind.Spec => "spec",
		BoardKind.Ideas => "idea",
		BoardKind.Intake => "issue",
		_ => "",
	};
}
