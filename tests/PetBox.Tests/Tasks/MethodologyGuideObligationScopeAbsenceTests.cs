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

	// A workflow whose very FIRST (Initial) status is ITSELF a gate's `From` — no shipped
	// preset does this today, but the guide must not assert a self-contradiction if one ever
	// does: "stuck in Initial is a defect" directly next to "waiting at Initial is the rule"
	// would be exactly the pressure-with-no-release-valve defect this card exists to remove.
	static readonly MethodologyKindDef InitialIsGateFromKind = new("approval-at-birth", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["item"],
		[
			new WorkflowStatus("draft", "Draft", StatusKind.Open),
			new WorkflowStatus("published", "Published", StatusKind.TerminalOk),
		],
		[new MethodologyTransitionDef("draft", "published", RequiresApproval: true)]),
	]);

	// ---- (a) obligation axis ----

	[Fact]
	public void GatedWorkflow_NamesTheObligationAxis_NextToTheProhibition()
	{
		var guide = Guide(Classic());

		// The prohibition half (pre-existing) and the obligation half (this card) must both be
		// present, scoped to open-to-open movement, and naming the workflow's Initial status
		// (not a blanket "any non-terminal status") as the concrete "stuck" shape.
		guide.Markdown.Should().Contain("The agent NEVER performs Review -> Done");
		guide.Markdown.Should().Contain("Every OTHER transition that moves work between two OPEN statuses is the agent's own to make");
		guide.Markdown.Should().Contain("A card left sitting in Backlog after the work behind it has already moved on is a defect.");
	}

	// review finding, defect 1: the ORIGINAL wording ("any other non-terminal status") swept up
	// Review itself — the status whose only forward exit is forbidden to the agent one line
	// above. That turned "stop here and hand over" into "you have a defect", pressure with no
	// legitimate release valve (the textbook shape for an agent to resolve by self-approving).
	// The fix must, in the SAME sentence, both narrow the "stuck" claim to Initial (Backlog,
	// not Review) AND say outright that waiting at the gate's own `From` (Review) is the rule.
	[Fact]
	public void GatedStatus_IsExemptFromTheStuckDefectClaim_NotSweptIntoIt()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().Contain(
			"stop at Review and hand over — waiting there is exactly the rule, not neglect.",
			"the gate's own From status must be named as the legitimate wait point, not left to be inferred");
		guide.Markdown.Should().NotContain("(or any other non-terminal status)",
			"the old blanket phrasing swept up Review — the very status the NEVER line just told the agent to stop at");
	}

	// The degenerate case: when Initial itself IS the gate's From, asserting "stuck in Initial
	// is a defect" would contradict "waiting at Initial is the rule" one clause earlier. The
	// stuck-claim must be omitted rather than printed self-contradictory.
	[Fact]
	public void WorkflowWhoseInitialIsItselfTheGateFrom_OmitsTheStuckClaim()
	{
		var guide = Guide(Classic() with { Kinds = [InitialIsGateFromKind] });

		guide.Markdown.Should().Contain("stop at draft and hand over — waiting there is exactly the rule, not neglect.");
		guide.Markdown.Should().NotContain("card left sitting in draft",
			"draft is both Initial AND the gate's From here — claiming it's a defect to sit there would contradict the same line's 'waiting there is the rule'");
	}

	// review finding, defect 2: "every OTHER transition ... is yours", read literally, granted
	// the agent transitions that decide the work's FATE (closing without delivery, reopening a
	// closed node) — not movement the work makes toward delivery. Cancel/duplicate/reopen are
	// not the agent's call any more than Done/accepted is; the grant must name them OUT.
	[Fact]
	public void ObligationGrant_ExcludesClosingAndReopeningTransitions()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().Contain("between two OPEN statuses is the agent's own to make",
			"the grant is scoped to open-to-open movement, not every transition the FSM happens to allow");
		guide.Markdown.Should().Contain(
			"Closing without delivery or reopening a closed node is a separate decision this line does not grant.");
		guide.Markdown.Should().NotContain("Every OTHER transition in this workflow is the agent's own to make",
			"the unscoped grant would literally include -> Cancelled and -> Duplicate, decisions about the work's fate");
	}

	[Fact]
	public void GateFreeWorkflow_NeverGetsTheObligationLine()
	{
		var guide = Guide(Classic() with { Kinds = [GateFreeKind] });

		guide.Markdown.Should().Contain("## Kind: wiki");
		guide.Markdown.Should().NotContain("is the agent's own to make",
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
