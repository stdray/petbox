using System.Text;
using PetBox.Tasks.Contract;

namespace PetBox.Tasks.Workflow;

// The machine-readable form of one process rule the guide derives from the methodology
// data (spec artifacts-from-definition): the same derivation the markdown renders as
// prose, kept structured so future consumers (memory invariants, artifact codegen) don't
// re-parse markdown. Rule ∈ approval_gate | reason_required | precondition_artifact |
// link_constraint | tag_axes; Detail is the rule's compact payload (e.g. "Review -> Done",
// "feature requires task_spec (specRef)", "area|concern").
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
			+ "Everything below (types, statuses, transitions, gates) is ENFORCED by the server; this guide describes the data, it adds no rules of its own.");

		foreach (var kind in runtime.EffectiveKinds())
			AppendKind(md, runtime, kind, invariants);

		AppendRelationKinds(md, runtime);
		return new MethodologyGuideView(md.ToString(), invariants, source, definitionVersion);
	}

	static void AppendKind(StringBuilder md, MethodologyRuntime runtime, MethodologyKindDef kind, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine($"## Kind: {kind.Kind}");
		md.AppendLine();

		// The quick-add default is the first type of the first block (declaration order is
		// meaningful — the same convention MethodologyRuntime.DefaultType applies).
		var defaultType = kind.Workflows[0].Types[0];
		var types = kind.Workflows.SelectMany(b => b.Types)
			.Select(t => string.Equals(t, defaultType, StringComparison.OrdinalIgnoreCase) ? $"{t} (default)" : t);
		md.AppendLine($"- Types: {string.Join(", ", types)}");
		md.AppendLine(kind.QuickAddAllowed
			? $"- Quick-add: allowed — an untyped quick-add creates a `{defaultType}`."
			: "- Quick-add: rejected — create nodes via tasks.upsert with the required fields.");

		var axes = runtime.TagAxes(kind.Kind);
		if (axes.Count > 0)
		{
			md.AppendLine($"- Tags: namespaced only — `<axis>:value` with the axis one of: {string.Join(", ", axes.Select(a => a.Namespace))}.");
			invariants.Add(new(kind.Kind, "tag_axes", string.Join("|", axes.Select(a => a.Namespace))));
		}
		else
			md.AppendLine("- Tags: free-form (no axes declared).");

		foreach (var block in kind.Workflows)
			AppendWorkflow(md, kind.Kind, block, invariants);

		if (kind.LinkConstraints is { Count: > 0 } constraints)
			AppendLinkConstraints(md, kind.Kind, constraints, invariants);
	}

	static void AppendWorkflow(StringBuilder md, string kind, MethodologyWorkflowDef block, List<MethodologyInvariant> invariants)
	{
		md.AppendLine();
		md.AppendLine($"### Workflow: {string.Join(" | ", block.Types)}");
		md.AppendLine();
		md.AppendLine($"- Statuses (initial: {block.Initial}):");
		AppendStatusGroup(md, block, StatusKind.Open, "open");
		AppendStatusGroup(md, block, StatusKind.TerminalOk, "terminal-ok (closes the node as delivered)");
		AppendStatusGroup(md, block, StatusKind.TerminalCancel, "terminal-cancel (closes the node without delivery)");
		AppendTransitions(md, block);
		AppendGates(md, kind, block, invariants);
	}

	static void AppendStatusGroup(StringBuilder md, MethodologyWorkflowDef block, StatusKind kind, string label)
	{
		var slugs = block.Statuses.Where(s => s.Kind == kind).Select(s => s.Slug).ToList();
		if (slugs.Count > 0)
			md.AppendLine($"  - {label}: {string.Join(", ", slugs)}");
	}

	static void AppendTransitions(StringBuilder md, MethodologyWorkflowDef block)
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
			if (t.RequiresApproval) marks.Add("OWNER-ONLY");
			if (t.RequiresReason) marks.Add("reason required");
			if (t.PreconditionArtifact is not null) marks.Add($"requires artifact:{t.PreconditionArtifact}");
			md.AppendLine($"  - {t.From} -> {t.To}{(marks.Count > 0 ? $" [{string.Join("; ", marks)}]" : "")}");
		}
	}

	// The gates as BEHAVIORAL invariants — the prose an agent must act on, each mirrored
	// into the structured list. Order: transitions in declaration order; a transition
	// carrying several gates emits them approval → reason → artifact.
	static void AppendGates(StringBuilder md, string kind, MethodologyWorkflowDef block, List<MethodologyInvariant> invariants)
	{
		var gated = block.Transitions.Where(t => t.RequiresApproval || t.RequiresReason || t.PreconditionArtifact is not null).ToList();
		if (gated.Count == 0) return;
		md.AppendLine("- GATES (behavioral invariants):");
		foreach (var t in gated)
		{
			if (t.RequiresApproval)
			{
				md.AppendLine($"  - The agent NEVER performs {t.From} -> {t.To} — that transition is the owner's/maintainer's call. Stop at {t.From} and hand over.");
				invariants.Add(new(kind, "approval_gate", $"{t.From} -> {t.To}"));
			}
			if (t.RequiresReason)
			{
				md.AppendLine($"  - {t.From} -> {t.To} requires a reason: provide it in the body of the status-changing upsert.");
				invariants.Add(new(kind, "reason_required", $"{t.From} -> {t.To}"));
			}
			if (t.PreconditionArtifact is not null)
			{
				md.AppendLine($"  - Add an `artifact:{t.PreconditionArtifact}` comment on the node before {t.From} -> {t.To} — the transition is rejected without it.");
				invariants.Add(new(kind, "precondition_artifact", $"{t.From} -> {t.To} requires artifact:{t.PreconditionArtifact}"));
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
			md.AppendLine($"- A new `{c.Type}` must carry a `{c.Link}` link (provide `{LinkField(c.Link)}` in the creating upsert). Edits don't re-require it.");
			invariants.Add(new(kind, "link_constraint", $"{c.Type} requires {c.Link} ({LinkField(c.Link)})"));
		}
	}

	static void AppendRelationKinds(StringBuilder md, MethodologyRuntime runtime)
	{
		md.AppendLine();
		md.AppendLine("## Relation kinds");
		md.AppendLine();
		md.AppendLine($"- Process (FSM effects and guards key on these): {string.Join(", ", MethodologyRuntime.ProcessRelationKinds)}");
		md.AppendLine($"- Neutral (free semantic edges, no process meaning): {string.Join(", ", MethodologyRuntime.NeutralRelationKinds)}");
		var declared = runtime.DeclaredLinkKinds;
		md.AppendLine(declared.Count > 0
			? $"- Project-declared (free semantic edges): {string.Join(", ", declared.Select(k => k.Description is null ? k.Slug : $"{k.Slug} ({k.Description})"))}"
			: "- Project-declared: none.");
	}

	// The upsert field that expresses a creation-gating link kind (the validator limits
	// constraints to exactly these three, so the fallback arm is defensive only).
	static string LinkField(string link) => link.ToLowerInvariant() switch
	{
		"task_spec" => "specRef",
		"blocks" => "blockedBy",
		"idea_spec" => "ideaRef",
		_ => link,
	};

	// A block whose transition set is the COMPLETE from≠to pairing over its statuses, with
	// no gates on any edge, models free transitions (MethodologyPresets.AllPairs). The
	// duplicate-edge validator makes the count check exact.
	static bool IsFreeTransitions(MethodologyWorkflowDef block)
	{
		var n = block.Statuses.Count;
		if (n < 2 || block.Transitions.Count != n * (n - 1)) return false;
		if (block.Transitions.Any(t => t.RequiresApproval || t.RequiresReason || t.PreconditionArtifact is not null)) return false;
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
