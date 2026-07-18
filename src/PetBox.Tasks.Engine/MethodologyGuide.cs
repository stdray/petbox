using System.Text;
using PetBox.Tasks.Contract;

namespace PetBox.Tasks.Workflow;

// The machine-readable form of one process rule the guide derives from the methodology
// data (spec artifacts-from-definition): the same derivation the markdown renders as
// prose, kept structured so future consumers (memory invariants, artifact codegen) don't
// re-parse markdown. Rule ∈ approval_gate (owner-only by convention) |
// approval_gate_enforced (the server blocks it) | reason_required (server-enforced, the
// default) | reason_required_convention (schema v2: Enforce.Artifacts:false — declared, not
// blocked) | precondition_artifact (server-enforced) | precondition_artifact_convention
// (Enforce.Artifacts:false) | checklist | transition_effect | link_constraint | tag_axes |
// auto_wire | delivery; Detail is the rule's compact payload (e.g. "Review -> Done",
// "feature requires task_spec (specRef)", "area|concern", "spec",
// "required:feature; defects:bug").
public sealed record MethodologyInvariant(string Kind, string Rule, string Detail);

// Renders a project's EFFECTIVE methodology — definition-declared kinds + preset kinds
// the definition does not override (MethodologyRuntime.EffectiveKinds) — as the
// agent-facing process guide: markdown prose plus the structured invariants behind it.
// Pure and deterministic (stable ordering: kinds in effective order, blocks/statuses/
// transitions in declaration order), and NOTHING here is specific to a particular kind:
// the built-in quartet and a user-defined `support` kind go through the same derivation.
// This is how the hardcoded "agent never self-sets Done/accepted" invariant becomes
// data-born: a RequiresApproval transition renders it, whatever the kind.
public static class MethodologyGuide
{
	public static MethodologyGuideView Render(string name, MethodologyRuntime runtime, string source, long? definitionVersion)
	{
		var md = new StringBuilder();
		var invariants = new List<MethodologyInvariant>();

		md.AppendLine($"# Process guide: {name}");
		md.AppendLine();
		md.AppendLine($"How to work this project's boards — derived at runtime from the project's methodology data (source: {source}). "
			+ "This guide describes the data, it adds no rules of its own. Types, statuses, transitions and gates are ENFORCED by the server "
			+ "unless a rule is explicitly marked as a convention (a convention gate or a checklist is binding process, but the server does not block it).");

		foreach (var kind in runtime.EffectiveKinds())
			AppendKind(md, runtime, kind, invariants);

		AppendRelationKinds(md, runtime);
		AppendBodyConventions(md);
		// A kind whose workflow is split into several blocks (types sharing one FSM each) can
		// carry the SAME status pair on two different blocks (e.g. a `ticket`/`incident` split
		// that both gate "Open -> Junk" with a reason) — the invariant's Detail names only the
		// status edge, not the block/type, so both blocks would otherwise contribute the
		// identical (Kind, Rule, Detail) tuple and a downstream consumer keying on the
		// invariants list would see the rule twice for one real rule. MethodologyInvariant is a
		// record (structural equality over Kind/Rule/Detail), so Distinct() is the exact dedup —
		// first occurrence wins, order preserved (Guide_IsDeterministic).
		return new MethodologyGuideView(md.ToString(), invariants.Distinct().ToList(), source, definitionVersion);
	}

	static void AppendKind(StringBuilder md, MethodologyRuntime runtime, MethodologyKindDef kind, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine($"## Kind: {kind.Kind}");
		md.AppendLine();

		if (kind.Description is { Length: > 0 } kindDescription)
		{
			md.AppendLine(kindDescription);
			md.AppendLine();
		}

		// The quick-add default is the first type of the first block (declaration order is
		// meaningful — the same convention MethodologyRuntime.DefaultType applies).
		var defaultType = kind.Workflows[0].Types[0];
		var types = kind.Workflows.SelectMany(b => b.Types)
			.Select(t => string.Equals(t, defaultType, StringComparison.OrdinalIgnoreCase) ? $"{t} (default)" : t);
		md.AppendLine($"- Types: {string.Join(", ", types)}");
		md.AppendLine(kind.QuickAddAllowed
			? $"- Quick-add: allowed — an untyped quick-add creates a `{defaultType}`."
			: "- Quick-add: rejected — create nodes via tasks_upsert with the required fields.");

		var axes = runtime.TagAxes(kind.Kind);
		if (axes.Count > 0)
		{
			var axisNames = axes.Select(a => a.Description is { Length: > 0 } d ? $"{a.Namespace} ({d})" : a.Namespace);
			md.AppendLine($"- Tags: namespaced only — `<axis>:value` with the axis one of: {string.Join(", ", axisNames)}.");
			invariants.Add(new(kind.Kind, "tag_axes", string.Join("|", axes.Select(a => a.Namespace))));
		}
		else
			md.AppendLine("- Tags: free-form (no axes declared).");

		var strictMode = runtime.StrictMode(kind.Kind);
		foreach (var block in kind.Workflows)
			AppendWorkflow(md, kind.Kind, block, strictMode, invariants);

		if (kind.LinkConstraints is { Count: > 0 } constraints)
			AppendLinkConstraints(md, kind.Kind, constraints, invariants);

		if (kind.Effects is { Count: > 0 } effects)
			AppendEffects(md, kind.Kind, effects, invariants);

		if (kind.AutoWireSpecFrom is { Length: > 0 } from)
			AppendAutoWire(md, kind.Kind, from, invariants);

		if (kind.Delivery is { } delivery)
			AppendDelivery(md, kind.Kind, delivery, invariants);
	}

	static void AppendWorkflow(StringBuilder md, string kind, MethodologyWorkflowDef block, bool strictMode, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine($"### Workflow: {string.Join(" | ", block.Types)}");
		md.AppendLine();
		md.AppendLine($"- Statuses (initial: {block.Initial}):");
		AppendStatusGroup(md, block, StatusKind.Open, "open");
		AppendStatusGroup(md, block, StatusKind.TerminalOk, "terminal-ok (closes the node as delivered)");
		AppendStatusGroup(md, block, StatusKind.TerminalCancel, "terminal-cancel (closes the node without delivery)");
		AppendTransitions(md, block, strictMode);
		AppendGates(md, kind, block, strictMode, invariants);
	}

	static void AppendStatusGroup(StringBuilder md, MethodologyWorkflowDef block, StatusKind kind, string label)
	{
		var statuses = block.Statuses.Where(s => s.Kind == kind).ToList();
		if (statuses.Count == 0) return;
		var rendered = statuses.Select(s => s.Description is { Length: > 0 } d ? $"{s.Slug} ({d})" : s.Slug);
		md.AppendLine($"  - {label}: {string.Join(", ", rendered)}");
	}

	static void AppendTransitions(StringBuilder md, MethodologyWorkflowDef block, bool strictMode)
	{
		if (IsFreeTransitions(block))
		{
			// The complete from≠to pairing with no gates IS "free transitions" (the `simple`
			// preset's shape) — say so instead of listing n·(n-1) edges.
			md.AppendLine($"- Transitions: free — any status may move to any other ({string.Join(" | ", block.Statuses.Select(s => s.Slug))}).");
			return;
		}
		md.AppendLine("- Transitions:");
		foreach (var t in block.Transitions)
		{
			var marks = new List<string>();
			if (t.RequiresApproval) marks.Add(t.EffectiveEnforceApproval(strictMode) ? "OWNER-ONLY (enforced)" : "OWNER-ONLY");
			var enforceArtifacts = t.EffectiveEnforceArtifacts();
			foreach (var a in t.EffectiveRequiredArtifacts())
				marks.Add(a.Inline
					? (enforceArtifacts ? "reason required" : "reason required (convention)")
					: (enforceArtifacts ? $"requires artifact:{a.Slug}" : $"requires artifact:{a.Slug} (convention)"));
			if (t.Checklist is { Count: > 0 }) marks.Add("checklist");
			if (t.Description is { Length: > 0 } d) marks.Add($"note: {d}");
			md.AppendLine($"  - {t.From} -> {t.To}{(marks.Count > 0 ? $" [{string.Join("; ", marks)}]" : "")}");
		}
	}

	// The gates as BEHAVIORAL invariants — the prose an agent must act on, each mirrored
	// into the structured list. Order: transitions in declaration order; a transition
	// carrying several gates emits them approval → reason → artifact → checklist.
	static void AppendGates(StringBuilder md, string kind, MethodologyWorkflowDef block, bool strictMode, List<MethodologyInvariant> invariants)
	{
		var gated = block.Transitions
			.Where(t => t.RequiresApproval || t.EffectiveRequiredArtifacts().Count > 0 || t.Checklist is { Count: > 0 })
			.ToList();
		if (gated.Count == 0) return;
		md.AppendLine("- GATES (behavioral invariants):");
		foreach (var t in gated)
		{
			if (t.RequiresApproval)
			{
				// The gate MODE, honestly: enforced = the server blocks a non-owner;
				// convention = the rule binds the agent but the server does not block it.
				var enforceApproval = t.EffectiveEnforceApproval(strictMode);
				var mode = enforceApproval
					? "owner-only (enforced by the server — it blocks the transition)"
					: "owner-only (convention — the server does not block it)";
				md.AppendLine($"  - The agent NEVER performs {t.From} -> {t.To} — that transition is the owner's/maintainer's call, {mode}. Stop at {t.From} and hand over.");
				invariants.Add(new(kind, enforceApproval ? "approval_gate_enforced" : "approval_gate", $"{t.From} -> {t.To}"));
			}
			var enforceArtifacts = t.EffectiveEnforceArtifacts();
			foreach (var a in t.EffectiveRequiredArtifacts())
			{
				// The artifacts gate MODE, honestly, same posture as approval above (spec
				// methodology-gate-strictness — the server's strictness must never be a secret
				// the guide keeps from the agent).
				var forceNote = enforceArtifacts ? "" : " (convention — the server does not block it)";
				if (a.Inline)
				{
					md.AppendLine($"  - {t.From} -> {t.To} requires a reason{forceNote}: provide the `reason` field on the status-changing upsert (never the node body).");
					invariants.Add(new(kind, enforceArtifacts ? "reason_required" : "reason_required_convention", $"{t.From} -> {t.To}"));
				}
				else
				{
					md.AppendLine($"  - Add an `artifact:{a.Slug}` comment on the node before {t.From} -> {t.To}{(enforceArtifacts ? " — the transition is rejected without it." : forceNote + ".")}");
					invariants.Add(new(kind, enforceArtifacts ? "precondition_artifact" : "precondition_artifact_convention", $"{t.From} -> {t.To} requires artifact:{a.Slug}"));
				}
			}
			if (t.Checklist is { Count: > 0 } checklist)
			{
				// Free-text conditions — a convention block, not server-checked.
				md.AppendLine($"  - Before {t.From} -> {t.To} confirm (convention — the server does not check these):");
				foreach (var item in checklist)
					md.AppendLine($"    - {item}");
				invariants.Add(new(kind, "checklist", $"{t.From} -> {t.To}: {string.Join(" | ", checklist)}"));
			}
		}
	}

	static void AppendLinkConstraints(StringBuilder md, string kind, IReadOnlyList<MethodologyLinkConstraintDef> constraints, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine("### Creation links");
		md.AppendLine();
		foreach (var c in constraints)
		{
			// Cadence follows the constraint's own data (mirrors RequireDefinitionLinks): a
			// constraint that pins a target STATUS is a PROVENANCE link required on every write; the
			// rest gate creation only. Every link is addressed through the generic `links.<kind>`
			// door — there are no sugar fields anymore.
			var everyWrite = c.TargetStatuses is { Count: > 0 };
			var cadence = everyWrite
				? $"- EVERY write of a `{c.Type}` must carry a `{c.Link}` link (provide `links.{c.Link}` in each upsert — it names the authorizing node)"
				: $"- A new `{c.Type}` must carry a `{c.Link}` link (provide `links.{c.Link}` in the creating upsert)";
			var note = c.Description is { Length: > 0 } d ? $" — {d}" : "";
			md.AppendLine($"{cadence}{TargetProse(c)}{note}.{(everyWrite ? "" : " Edits don't re-require it.")}");
			invariants.Add(new(kind, "link_constraint", $"{c.Type} requires {c.Link} (links.{c.Link}){TargetDetail(c)}"));
		}
	}

	// The declared link target as prose ("must link a <kind> node in status <s1|s2>") —
	// empty when the constraint declares none (the pre-v2 line, verbatim).
	static string TargetProse(MethodologyLinkConstraintDef c) =>
		(c.TargetKind, c.TargetStatuses) switch
		{
			(null, null or { Count: 0 }) => "",
			({ } k, null or { Count: 0 }) => $" — it must link a `{k}` node",
			(null, { } s) => $" — it must link a node in status {string.Join("|", s)}",
			({ } k, { } s) => $" — it must link a `{k}` node in status {string.Join("|", s)}",
		};

	// The same target compactly for the machine invariant (e.g. " -> spec[defined]").
	static string TargetDetail(MethodologyLinkConstraintDef c) =>
		c.TargetKind is null && c.TargetStatuses is not { Count: > 0 }
			? ""
			: $" -> {c.TargetKind ?? "*"}{(c.TargetStatuses is { Count: > 0 } s ? $"[{string.Join("|", s)}]" : "")}";

	// Declared transition effects, one line each — the engine EXECUTES them when a node of
	// this kind enters (default) or leaves (OnLeave, Effect.onLeave) the trigger status
	// (TaskTransitionEffects.RunTransitionEffectsAsync).
	static void AppendEffects(StringBuilder md, string kind, IReadOnlyList<MethodologyTransitionEffectDef> effects, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine("### Transition effects");
		md.AppendLine();
		md.AppendLine("Cross-node automation the SERVER executes when a node of this kind enters or leaves the trigger status — do not apply these by hand:");
		foreach (var e in effects)
		{
			md.AppendLine($"- {EffectSentence(e, markdown: true)}");
			invariants.Add(new(kind, "transition_effect",
				$"{(e.OnLeave ? "leave " : "")}{e.On}: {e.Direction} {e.Link}{(e.OnlyFrom is null ? "" : $" from {e.OnlyFrom}")} -> {e.Set ?? "(closed)"}"));
		}
	}

	// ONE phrasing for a declared transition effect, shared by the guide markdown (link in
	// backticks) and plain-text surfaces (the methodology editor's per-kind effects
	// annotation) — so "On entering Done, incoming issue_task nodes are set to done." reads
	// identically wherever effects surface. `Set: null` (Effect.onLeave's pure edge-consumption
	// shape) reads as "have the link closed" instead of a status.
	public static string EffectSentence(MethodologyTransitionEffectDef e, bool markdown = false)
	{
		var link = markdown ? $"`{e.Link}`" : e.Link;
		var scope = e.OnlyFrom is null ? "" : $" currently in {e.OnlyFrom}";
		var trigger = e.OnLeave ? "leaving" : "entering";
		var action = e.Set is null ? "have the link closed" : $"are set to {e.Set}";
		var note = e.Description is { Length: > 0 } d ? $" ({d})" : "";
		return $"On {trigger} {e.On}, {e.Direction} {link} nodes{scope} {action}.{note}";
	}

	// SpecBoard auto-wire as DATA (primitives-enum-residual) — when exactly one board of
	// each kind is active and SpecBoard is empty, the server wires them.
	static void AppendAutoWire(StringBuilder md, string kind, string fromKind, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine("### Auto-wire");
		md.AppendLine();
		md.AppendLine($"- When exactly one active `{kind}` board and one active `{fromKind}` board exist and SpecBoard is empty, the server auto-wires SpecBoard of the `{kind}` board to the `{fromKind}` board.");
		invariants.Add(new(kind, "auto_wire", fromKind));
	}

	// Delivery type roles as DATA (primitives-enum-residual) — the feature/bug quartet rule
	// is just requiredTypes=feature, defectTypes=bug on the preset.
	static void AppendDelivery(StringBuilder md, string kind, MethodologyDeliveryDef delivery, List<MethodologyInvariant> invariants)
	{
		var required = string.Join("|", delivery.RequiredTypes);
		var defects = string.Join("|", delivery.DefectTypes);
		md.AppendLine();
		md.AppendLine("### Delivery roll-up");
		md.AppendLine();
		md.AppendLine($"- Delivery is COMPUTED from inbound `{delivery.Link}` links (and rolled up the part_of tree):");
		md.AppendLine($"  - required types ({required}): none → not_started; any non-terminal-ok → in_progress; all terminal-ok → done candidate");
		md.AppendLine(defects.Length > 0
			? $"  - defect types ({defects}): any still open while requireds are done → done_with_defects"
			: "  - no defect types declared — done has no defect variant");
		invariants.Add(new(kind, "delivery", $"required:{required}; defects:{defects}"));
	}

	static void AppendRelationKinds(StringBuilder md, MethodologyRuntime runtime)
	{
		md.AppendLine();
		md.AppendLine("## Relation kinds");
		md.AppendLine();
		md.AppendLine($"- Structural (FSM effects and guards key on these, direction-less builtins): {string.Join(", ", MethodologyRuntime.ProcessRelationKinds)}");
		md.AppendLine($"- Neutral (free semantic edges, no process meaning): {string.Join(", ", MethodologyRuntime.NeutralRelationKinds)}");
		// The directed link kinds (the quartet's declared process trio + any project-declared kind),
		// each with its category and stored-edge orientation — addressed via links:{kind:ref}.
		var declared = runtime.EffectiveLinkKinds();
		md.AppendLine(declared.Count > 0
			? $"- Declared (address via links:{{kind:ref}}): {string.Join(", ", declared.Select(RenderDeclaredLinkKind))}"
			: "- Declared: none.");
	}

	// One declared relation kind as human-readable text. A direction-less NEUTRAL kind keeps the
	// bare `slug (description)` shape (the free-edge default); a kind that carries a Direction or
	// is Process-categorized adds a bracketed suffix with its category and stored-edge orientation
	// (fromKind → toKind (Label)), a `*` marking an unconstrained end (spec methodology-link-kinds-
	// declared). FromKind/ToKind read as the STORED edge (relations.from → to), Label the semantics.
	static string RenderDeclaredLinkKind(MethodologyLinkKindDef k)
	{
		var head = k.Description is null ? k.Slug : $"{k.Slug} ({k.Description})";
		if (k.Category == LinkCategory.Neutral && k.Direction is null)
			return head;
		var parts = new List<string> { k.Category.ToString().ToLowerInvariant() };
		if (k.Direction is { } dir)
		{
			var from = dir.FromKind ?? "*";
			var to = dir.ToKind ?? "*";
			parts.Add(dir.Label is { Length: > 0 } ? $"{from} → {to} ({dir.Label})" : $"{from} → {to}");
		}
		return $"{head} [{string.Join("; ", parts)}]";
	}

	static void AppendBodyConventions(StringBuilder md)
	{
		md.AppendLine();
		md.AppendLine("## Body conventions");
		md.AppendLine();
		md.AppendLine("Node bodies and comment bodies render as GFM markdown (marked+DOMPurify, gfm:true, breaks:true).");
		md.AppendLine("- Use `## Heading` for top-level headings, NOT `==Heading==` or `--Heading--` — these ASCII decorations don't render as GFM headings.");
		md.AppendLine("- Use `### Sub-heading` for nested sections.");
		md.AppendLine("- Numbered lists as `1. item` (the renderer auto-numbers).");
		md.AppendLine("- Real newlines separate paragraphs; a bare newline becomes a `<br>` — never write `\\n` literals.");
		md.AppendLine("- Code blocks with triple backticks: ` ``` ` (language tag optional).");
		md.AppendLine("- Inline code with single backticks: `` `code` ``.");
	}

	// A block whose transition set is the COMPLETE from≠to pairing over its statuses, with
	// no gates on any edge, models free transitions (MethodologyPresets.AllPairs). The
	// duplicate-edge validator makes the count check exact.
	static bool IsFreeTransitions(MethodologyWorkflowDef block)
	{
		var n = block.Statuses.Count;
		if (n < 2 || block.Transitions.Count != n * (n - 1)) return false;
		if (block.Transitions.Any(t => t.RequiresApproval || t.EffectiveRequiredArtifacts().Count > 0 || t.Checklist is { Count: > 0 })) return false;
		var edges = block.Transitions
			.Select(t => (From: t.From.ToLowerInvariant(), To: t.To.ToLowerInvariant()))
			.ToHashSet();
		return block.Statuses
			.SelectMany(a => block.Statuses
				.Where(b => !string.Equals(a.Slug, b.Slug, StringComparison.OrdinalIgnoreCase))
				.Select(b => (From: a.Slug.ToLowerInvariant(), To: b.Slug.ToLowerInvariant())))
			.All(edges.Contains);
	}
}
