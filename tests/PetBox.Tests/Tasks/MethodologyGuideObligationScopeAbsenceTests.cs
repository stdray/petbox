using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// guide-states-obligation-scope-and-absence: three independent guide-text defects closed by
// one card (a) the guide prints the PROHIBITION half of a gate (the owner-only NEVER line)
// but never the OBLIGATION half — an agent reads the one ban expansively and freezes every
// status move (kek-devices-classic-status-freeze, recurrence 2, open client report); (b) the
// "Relation kinds" dictionary is sourced from MethodologyRuntime.EffectiveLinkKinds(), which
// unconditionally concatenates the quartet trio + observation-promotion fallback for
// RESOLUTION reasons — the GUIDE must not mirror that leak
// (guide-leaks-quartet-link-kinds-into-projects-without-them); (c) "Declaring your own kind"
// named the verbs but not one knob, so an agent that found `type` by pattern-matching had no
// way to discover `blocksGate`/`effects` existed at all
// (guide-mirrors-declared-never-lists-available-knobs).
public sealed class MethodologyGuideObligationScopeAbsenceTests
{
	static MethodologyDefinition Classic() => MethodologyPresets.RenderPresetDefinition("classic");
	static MethodologyDefinition Quartet() => MethodologyPresets.RenderPresetDefinition("quartet");

	static MethodologyGuideView Guide(MethodologyDefinition def, string source = "instance") =>
		MethodologyGuide.Render(def.Name, new MethodologyRuntime(def), source, 1);

	// The "## Relation kinds" section alone. Several slugs the (b) tests check for ALSO appear,
	// legitimately, in an unrelated section whenever the OWNING kind itself renders — e.g. a
	// rendered `spec` kind's own "### Creation links" always mentions `idea_spec` (that
	// requirement is the spec kind's OWN data, not the cross-kind dictionary this card
	// filters), and a rendered `work` kind's own "### Transition effects" always mentions
	// `issue_task`. Scoping to this one section is what makes a Contains/NotContains on a bare
	// slug name mean "in the dictionary this card filters", not "anywhere in the whole guide".
	static string RelationKindsSection(MethodologyGuideView guide)
	{
		var start = guide.Markdown.IndexOf("## Relation kinds", StringComparison.Ordinal);
		start.Should().BeGreaterThanOrEqualTo(0, "the guide always renders a Relation kinds section");
		var next = guide.Markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
		return next >= 0 ? guide.Markdown[start..next] : guide.Markdown[start..];
	}

	// A kind with a workflow that has NO approval gate anywhere — the control for (a): the
	// obligation line is conditioned on "this block has an owner-only NEVER line to qualify",
	// so a gate-free kind must never grow it (nothing to qualify, and a blanket "everything is
	// yours" on every kind would just be noise).
	static readonly MethodologyKindDef GateFreeKind = new("wiki", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["page"],
		[
			new WorkflowStatus("draft", "Draft", StatusKind.Open),
			new WorkflowStatus("published", "Published", StatusKind.TerminalOk),
		],
		[new MethodologyTransitionDef("draft", "published")]),
	]);

	// ---- (a) obligation axis ----

	[Fact]
	public void GatedWorkflow_NamesTheObligationAxis_NextToTheProhibition()
	{
		var guide = Guide(Classic());

		// The prohibition half (pre-existing) and the obligation half (this card) must both be
		// present, and the obligation line must name the concrete defect shape, not just
		// "everything else is fine" — a vague reassurance is exactly as unfalsifiable as silence.
		guide.Markdown.Should().Contain("The agent NEVER performs Review -> Done");
		guide.Markdown.Should().Contain("Every OTHER transition in this workflow is the agent's own to make");
		guide.Markdown.Should().Contain("is exactly that defect");
	}

	[Fact]
	public void GateFreeWorkflow_NeverGetsTheObligationLine()
	{
		var guide = Guide(Classic() with { Kinds = [GateFreeKind] });

		guide.Markdown.Should().Contain("## Kind: wiki");
		guide.Markdown.Should().NotContain("Every OTHER transition in this workflow is the agent's own to make",
			"a workflow with no approval gate has no prohibition for this line to qualify");
	}

	// ---- (b) relation-kind dictionary scoped to declared kind ends ----

	[Fact]
	public void ClassicInstance_RelationKinds_CarriesNoQuartetOrObservationEntries()
	{
		var guide = Guide(Classic());

		// None of the four builtin fallback link kinds name a board this classic-only project
		// has: idea_spec (ideas/spec), task_spec (work/spec), issue_task (intake/work),
		// observation_obligation (work-or-ideas). All four must be gone from the DECLARED
		// dictionary line, not merely absent from a `## Kind:` heading.
		var section = RelationKindsSection(guide);
		section.Should().NotContain("idea_spec");
		section.Should().NotContain("task_spec");
		section.Should().NotContain("issue_task");
		section.Should().NotContain("observation_obligation");
		section.Should().Contain("- Declared: none — this instance has no structural link kinds of its own.");

		// The card names this exact second half of the same leak: classic has no BlocksGate, so
		// nothing here actually keys an effect or a guard on `blocks` — the STRUCTURAL builtins
		// line is a fixed, direction-less vocabulary (unlike the declared dictionary above) and
		// is unaffected by this card; it is asserted here only so a future edit to it is not
		// mistaken for covering (b).
		section.Should().Contain("Structural (FSM effects and guards key on these, direction-less builtins): blocks, part_of, supersedes");
	}

	[Fact]
	public void QuartetInstance_RelationKinds_KeepsAllFourDeclaredEntries()
	{
		// The regression guard: a project that actually HAS ideas/spec/work/intake boards must
		// not lose the dictionary entries whose ends it legitimately has — this card fixes a
		// LEAK, not the dictionary itself.
		var section = RelationKindsSection(Guide(Quartet()));

		section.Should().Contain("idea_spec");
		section.Should().Contain("task_spec");
		section.Should().Contain("issue_task");
		section.Should().Contain("observation_obligation");
		section.Should().NotContain("- Declared: none");
	}

	// A project that declares work+spec but not ideas/intake (a partial quartet) keeps exactly
	// the entries whose BOTH ends it has, and observation_obligation (satisfied by `work` alone)
	// — the precise "declared ends" semantics the card asks for, not a blanket
	// all-or-nothing switch keyed on "is this the classic preset".
	[Fact]
	public void PartialQuartetInstance_KeepsOnlyEntriesWhoseBothEndsAreDeclared()
	{
		// Built from MethodologyPresets.KindDef directly (not RenderPresetDefinition("quartet")
		// with Kinds trimmed down): RenderPresetDefinition bakes the FULL trio into the
		// document's OWN LinkKinds the moment BoardKind.Work is among its Kinds — trimming Kinds
		// afterwards leaves that stale LinkKinds field untouched, which is a real "declared it,
		// then dropped the kind" case but not the one this fallback-filter path exists for. A
		// document with the default empty LinkKinds (never declared any of its own) is the
		// actual shape a hand-authored partial instance has, and is what exercises
		// EffectiveLinkKinds()'s builtin-fallback branch this card filters.
		var workAndSpecOnly = new MethodologyDefinition("custom",
			[MethodologyPresets.KindDef(BoardKind.Work), MethodologyPresets.KindDef(BoardKind.Spec)]);
		var section = RelationKindsSection(Guide(workAndSpecOnly));

		section.Should().Contain("task_spec", "both its ends (work, spec) are declared here");
		section.Should().Contain("observation_obligation", "its work-or-ideas end is satisfied by work alone");
		section.Should().NotContain("idea_spec", "its `ideas` end is not declared here");
		section.Should().NotContain("issue_task", "its `intake` end is not declared here");
	}

	// ---- (c) available-but-undeclared knobs ----

	[Fact]
	public void DeclaringYourOwnKind_ListsEveryKnobByName()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().Contain("## Declaring your own kind");
		foreach (var knob in new[] { "blocksGate", "effects", "linkConstraints", "tagAxes", "singleton" })
			guide.Markdown.Should().Contain($"`{knob}`", $"the knob {knob} exists on MethodologyKindInput but had no representation anywhere the agent looks");
		guide.Markdown.Should().Contain("tasks_methodology_rules_upsert");
	}
}
