namespace PetBox.Tasks.Workflow;

// Code-first preset catalog: board kind -> task type(s) -> state machine.
// First iteration seeds these in code (no per-project storage / editor yet).
// The engine reads them as data, so a future DSL/UI just supplies the same shapes.
public static class WorkflowCatalog
{
	public static BoardKind ParseKind(string? kind) =>
		Enum.TryParse<BoardKind>(kind, ignoreCase: true, out var k) ? k : BoardKind.Free;

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

	static readonly Workflow Spec = new("spec",
		[
			new("draft", "Draft", StatusKind.Open),
			new("defined", "Defined", StatusKind.Open),
			new("deprecated", "Deprecated", StatusKind.TerminalCancel),
		],
		[
			new("draft", "defined"),
			new("defined", "draft"),
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

	static readonly string[] WorkTypes = ["feature", "bug"];

	// The workflow for a (kind, type). Null = no workflow (kind=Free, or an unknown
	// work type) — Free means "no validation"; a null on Work is a "type required" error.
	public static Workflow? For(BoardKind kind, string? type) => kind switch
	{
		BoardKind.Free => null,
		BoardKind.Spec => Spec,
		BoardKind.Ideas => Idea,
		BoardKind.Intake => Issue,
		BoardKind.Work => WorkTypes.Contains((type ?? "").ToLowerInvariant()) ? Work(type!.ToLowerInvariant()) : null,
		_ => null,
	};

	// All workflows hosted by a board kind (for the tasks.workflow discovery tool).
	public static IReadOnlyList<Workflow> Types(BoardKind kind) => kind switch
	{
		BoardKind.Spec => [Spec],
		BoardKind.Ideas => [Idea],
		BoardKind.Intake => [Issue],
		BoardKind.Work => [Work("feature"), Work("bug")],
		_ => [],
	};

	static readonly BoardKind[] AllKinds = [BoardKind.Spec, BoardKind.Ideas, BoardKind.Intake, BoardKind.Work];

	// StatusKind for a status slug across ALL presets (case-insensitive), or null if
	// the slug isn't in any workflow (e.g. a free board's arbitrary status).
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
		BoardKind.Work => string.Join("|", WorkTypes),
		BoardKind.Spec => "spec",
		BoardKind.Ideas => "idea",
		BoardKind.Intake => "issue",
		_ => "",
	};
}
