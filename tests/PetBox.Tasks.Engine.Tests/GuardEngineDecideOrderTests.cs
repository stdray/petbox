using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// Decide's CONTRACT, as opposed to the individual guards': it runs the stages in the historical
// order and STOPS at the first refusal. With specRef/ideaRef gone the three per-sugar resolvers
// collapsed into ONE ResolveLinks stage, so the order is now:
//   ResolveLinks -> RequireDefinitionLinks -> ValidateLinkTargets -> RequireBlockers ->
//   RequirePreconditionArtifacts.
//
// The order is the ORDER OF INDICTMENT: the accused node's key is what the service's partial-mode
// retry loop retires from the batch, so a reordering silently changes WHICH node gets dropped.
public sealed class GuardEngineDecideOrderTests
{
	static MethodologyEngineDecision Decide(
		MethodologyEngineContext ctx, NodeState[] desired, Dictionary<string, NodeState>? prior = null,
		Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? links = null,
		Dictionary<string, string>? blockedBy = null) =>
		GuardEngine.Decide(ctx, desired, prior ?? NoPrior, links ?? NoLinks, blockedBy ?? NoRefs);

	// ---- The clean path ----

	[Fact]
	public void ACleanBatch_ProducesNoVerdicts_AndTheResolvedLinks()
	{
		var ctx = Ctx(index: [Node("s1", SpecBoardName, "spec", "auth", "defined", "spec")]);
		var prior = Prior(State("blocker", "InProgress", "chore", nodeId: Id("b1")));
		var d = Decide(ctx,
			[State("t1", "Blocked", "feature", nodeId: Id("t1"))],
			prior,
			links: Link("t1", "task_spec", "auth"),
			blockedBy: Refs(("t1", "blocker")));

		d.Verdicts.Should().BeEmpty();
		d.Links.Should().Contain(l => l.Kind == "task_spec" && l.WriterKey == "t1" && l.TargetNodeId == Id("s1") && l.WriterIsFrom);
		d.Links.Should().Contain(l => l.Kind == "blocks" && l.WriterKey == "t1" && l.TargetNodeId == Id("b1") && !l.WriterIsFrom);
	}

	[Fact]
	public void ARefusal_CarriesExactlyOneVerdict_AndNoLinks()
	{
		var d = Decide(Ctx(), [State("t1", "Pending", "feature")], links: Link("t1", "task_spec", "ghost"));
		d.Verdicts.Should().HaveCount(1);
		d.Links.Should().BeEmpty();
	}

	// ---- The order of indictment, stage by adjacent stage ----

	[Fact]
	public void BlockedByResolution_OutranksLinksResolution()
	{
		// Both fail to resolve; the blockedBy sugar is resolved first inside ResolveLinks.
		var d = Decide(Ctx(),
			[State("t1", "Pending", "feature", nodeId: Id("t1"))],
			links: Link("t1", "task_spec", "no-such-spec"),
			blockedBy: Refs(("t1", "no-such-blocker")));
		d.Verdicts.Single().Message.Should().StartWith("blockedBy 'no-such-blocker'");
	}

	[Fact]
	public void LinksResolution_OutranksRequireDefinitionLinks()
	{
		// `t1` has an unresolvable task_spec; `t2` provides none at all. The resolver speaks first.
		var ctx = Ctx();
		var d = Decide(ctx,
			[State("t1", "Pending", "feature", nodeId: Id("t1")), State("t2", "Pending", "feature", nodeId: Id("t2"))],
			links: Link("t1", "task_spec", "no-such-spec"));
		var v = d.Verdicts.Single();
		v.Node.Should().Be("t1");
		v.Message.Should().StartWith("links.task_spec 'no-such-spec'");
	}

	[Fact]
	public void RequireDefinitionLinks_OutranksValidateLinkTargets()
	{
		// `t1` provides a task_spec pointing at a non-spec node (a target violation); `t2` provides
		// none at all (a definition-link violation). RequireDefinitionLinks runs first, so `t2` speaks.
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other", "Pending", "chore")]);
		var d = Decide(ctx,
			[State("t1", "Pending", "feature", nodeId: Id("t1")), State("t2", "Pending", "feature", nodeId: Id("t2"))],
			links: Link("t1", "task_spec", Id("w2")));
		var v = d.Verdicts.Single();
		v.Node.Should().Be("t2");
		v.Message.Should().Contain("must carry a task_spec link — provide links.task_spec");
	}

	[Fact]
	public void ValidateLinkTargets_OutranksRequireBlockers()
	{
		// `t1` names a bad task_spec target AND sits in Blocked with no blocker: two verdicts are
		// available for the same node, and the target rule is the one that speaks.
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other", "Pending", "chore")]);
		var d = Decide(ctx,
			[State("t1", "Blocked", "feature", nodeId: Id("t1"))],
			links: Link("t1", "task_spec", Id("w2")));
		var v = d.Verdicts.Single();
		v.Node.Should().Be("t1");
		v.Message.Should().Contain("which is not a spec board");
		v.Kind.Should().Be(VerdictKind.InvalidArgument);
	}

	[Fact]
	public void RequireBlockers_OutranksRequirePreconditionArtifacts()
	{
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
