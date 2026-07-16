using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// Decide's CONTRACT, as opposed to the individual guards': it runs the seven stages in the
// historical order and STOPS at the first refusal.
//
// The order is the ORDER OF INDICTMENT. A batch that breaks several rules at once must be accused
// by the first stage in that sequence, not by whichever guard happens to run first after a
// refactor — because the accused node's key is what the service's partial-mode retry loop retires
// from the batch (ex.RejectedNode -> TemporalStore.Cascade), so a reordering silently changes
// WHICH node gets dropped and which dependents cascade with it. Until this file, that order was
// guaranteed only by the shape of Decide's method body.
public sealed class GuardEngineDecideOrderTests
{
	static MethodologyEngineDecision Decide(
		MethodologyEngineContext ctx, NodeState[] desired, Dictionary<string, NodeState>? prior = null,
		Dictionary<string, string>? specRefs = null, Dictionary<string, string>? blockedBy = null,
		Dictionary<string, string>? ideaRefs = null) =>
		GuardEngine.Decide(ctx, desired, prior ?? [], specRefs ?? NoRefs, blockedBy ?? NoRefs, ideaRefs ?? NoRefs);

	// ---- The clean path ----

	[Fact]
	public void ACleanBatch_ProducesNoVerdicts_AndTheResolvedRefMaps()
	{
		var ctx = Ctx(index: [Node("s1", SpecBoardName, "spec", "auth", "defined", "spec")]);
		var prior = Prior(State("blocker", "InProgress", "chore", nodeId: Id("b1")));
		var d = Decide(ctx,
			[State("t1", "Blocked", "feature", nodeId: Id("t1"))],
			prior,
			specRefs: Refs(("t1", "auth")),
			blockedBy: Refs(("t1", "blocker")));

		d.Verdicts.Should().BeEmpty();
		d.SpecRefs.Should().Equal(new Dictionary<string, string> { ["t1"] = Id("s1") });
		d.BlockedBy.Should().Equal(new Dictionary<string, string> { ["t1"] = Id("b1") });
		d.IdeaRefs.Should().BeEmpty();
	}

	[Fact]
	public void ARefusal_CarriesExactlyOneVerdict_AndNoRefMaps()
	{
		// Fail-fast: the ref maps of a refused decision are meaningless, so they are empty rather
		// than half-resolved — a half-resolved map is how a later guard would "discover" a
		// violation that never existed.
		var d = Decide(Ctx(), [State("t1", "Pending", "feature")], specRefs: Refs(("t1", "ghost")));
		d.Verdicts.Should().HaveCount(1);
		d.SpecRefs.Should().BeEmpty();
		d.BlockedBy.Should().BeEmpty();
		d.IdeaRefs.Should().BeEmpty();
	}

	// ---- The order of indictment, stage by adjacent stage ----

	[Fact]
	public void SpecRefResolution_OutranksBlockedByResolution()
	{
		var d = Decide(Ctx(),
			[State("t1", "Pending", "feature")],
			specRefs: Refs(("t1", "no-such-spec")),
			blockedBy: Refs(("t1", "no-such-blocker")));
		d.Verdicts.Single().Message.Should().StartWith("specRef 'no-such-spec'");
	}

	[Fact]
	public void BlockedByResolution_OutranksIdeaRefResolution()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName, specBoard: null);
		var d = Decide(ctx,
			[State("s1", "defined", "spec")],
			blockedBy: Refs(("s1", "no-such-blocker")),
			ideaRefs: Refs(("s1", "no-such-idea")));
		d.Verdicts.Single().Message.Should().StartWith("blockedBy 'no-such-blocker'");
	}

	[Fact]
	public void IdeaRefResolution_OutranksRequireDefinitionLinks()
	{
		// `s1` has an unresolvable ideaRef; `s2` has none at all. The resolver speaks first — even
		// though RequireDefinitionLinks would indict a DIFFERENT node.
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		var d = Decide(ctx,
			[State("s1", "defined", "spec"), State("s2", "defined", "spec")],
			ideaRefs: Refs(("s1", "no-such-idea")));
		var v = d.Verdicts.Single();
		v.Node.Should().Be("s1");
		v.Message.Should().StartWith("ideaRef 'no-such-idea'");
	}

	[Fact]
	public void RequireDefinitionLinks_OutranksValidateLinkTargets()
	{
		// `t1` provides a specRef pointing at a non-spec node (a target violation); `t2` provides
		// none at all (a definition-link violation). RequireDefinitionLinks runs first, so `t2` is
		// the one retired from the batch.
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other", "Pending", "chore")]);
		var d = Decide(ctx,
			[State("t1", "Pending", "feature"), State("t2", "Pending", "feature")],
			specRefs: Refs(("t1", Id("w2"))));
		var v = d.Verdicts.Single();
		v.Node.Should().Be("t2");
		v.Message.Should().Be("a work feature must link a spec node — provide specRef (node 't2')");
	}

	[Fact]
	public void ValidateLinkTargets_OutranksRequireBlockers()
	{
		// `t1` names a bad specRef target AND sits in Blocked with no blocker: two verdicts are
		// available for the same node, and the target rule is the one that speaks.
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other", "Pending", "chore")]);
		var d = Decide(ctx,
			[State("t1", "Blocked", "feature", nodeId: Id("t1"))],
			specRefs: Refs(("t1", Id("w2"))));
		var v = d.Verdicts.Single();
		v.Node.Should().Be("t1");
		v.Message.Should().Contain("which is not a spec board");
		v.Kind.Should().Be(VerdictKind.InvalidArgument);
	}

	[Fact]
	public void RequireBlockers_OutranksRequirePreconditionArtifacts()
	{
		// Needs one kind that is BOTH `work` (the blocker invariant) and gated (the artifact gate),
		// which no preset is — so the methodology says so as data.
		var ctx = Ctx(runtime: GatedWork, kindSlug: "work", specBoard: null);
		var prior = Prior(State("t1", "InProgress", "task", nodeId: Id("t1")));
		var d = Decide(ctx, [State("t1", "Blocked", "task", nodeId: Id("t1"))], prior);
		var v = d.Verdicts.Single();
		v.Message.Should().Be("a Blocked task must name a blocker — provide blockedBy (node 't1')");
		v.Kind.Should().Be(VerdictKind.InvalidArgument);
	}

	[Fact]
	public void RequirePreconditionArtifacts_SpeaksWhenNothingBeforeItDoes()
	{
		// Same context, blocker supplied: the last stage is now the one left with a complaint.
		var ctx = Ctx(runtime: GatedWork, kindSlug: "work", specBoard: null);
		var prior = Prior(State("t1", "InProgress", "task", nodeId: Id("t1")));
		var d = Decide(ctx, [State("t1", "Blocked", "task", nodeId: Id("t1"))], prior, blockedBy: Refs(("t1", Id("b1"))));
		var v = d.Verdicts.Single();
		v.Message.Should().Be("transition 'InProgress' -> 'Blocked' on node 't1' requires an artifact:triage_note comment (the transition's precondition artifact) — add the comment, then retry");
		v.Kind.Should().Be(VerdictKind.InvalidOperation);
	}

	// A `work` kind whose InProgress -> Blocked edge carries an artifact gate: the only shape in
	// which the blocker invariant and the artifact gate can both fire on one node.
	static readonly MethodologyRuntime GatedWork = MethodologyRuntime.From(new MethodologyDefinition("gated-work",
	[
		new MethodologyKindDef("work", QuickAddAllowed: false,
		[
			new MethodologyWorkflowDef(["task"],
				[
					new WorkflowStatus("InProgress", "In progress", StatusKind.Open),
					new WorkflowStatus("Blocked", "Blocked", StatusKind.Open),
					new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
				],
				[
					new MethodologyTransitionDef("InProgress", "Blocked", PreconditionArtifact: "triage_note"),
					new MethodologyTransitionDef("Blocked", "InProgress"),
					new MethodologyTransitionDef("InProgress", "Done"),
				]),
		]),
	]));
}
