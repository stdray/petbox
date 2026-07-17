using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// The precondition-artifact gate: a transition whose definition names a PreconditionArtifact only
// fires when the node carries an active `artifact:<slug>` comment. This is the guard that used to
// read the comment store in the MIDDLE of its judgement (04-doc, seam 1); it now judges over
// prefetched CommentTagsByNodeId, and NeedsCommentTags is how the engine DECLARES that the read is
// worth paying for. These verdicts are InvalidOperation, not InvalidArgument — the payload is
// well-formed, the process refuses it.
public sealed class GuardEnginePreconditionArtifactTests
{
	static readonly Dictionary<string, NodeState> Exploring = Prior(State("i1", "exploring", "idea", nodeId: Id("i1")));

	static MethodologyEngineContext IdeasCtx(params (string NodeId, string[] Tags)[] tags) =>
		Ctx(kindSlug: "ideas", board: IdeasBoardName, commentTags: Edges(tags));

	// ---- NeedsCommentTags: the engine declaring what the IO layer must prefetch ----

	[Fact]
	public void NeedsCommentTags_IsTrueOnlyForAKindThatGatesSomeTransition()
	{
		GuardEngine.NeedsCommentTags(Quartet, "ideas").Should().BeTrue("ideas gates exploring->review on artifact:spec_plan");
		GuardEngine.NeedsCommentTags(Quartet, "work").Should().BeFalse();
		GuardEngine.NeedsCommentTags(Quartet, "spec").Should().BeFalse();
		GuardEngine.NeedsCommentTags(Quartet, "intake").Should().BeFalse();
		GuardEngine.NeedsCommentTags(Presets, "ideas").Should().BeTrue();
		GuardEngine.NeedsCommentTags(Presets, null).Should().BeFalse();
	}

	// ---- The gate itself ----

	[Fact]
	public void GatedTransition_WithoutTheArtifactComment_IsRefused()
	{
		var v = GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "review", "idea")], Exploring);
		v.Should().Be(new MethodologyVerdict("i1",
			"transition 'exploring' -> 'review' on node 'i1' requires an artifact:spec_plan comment (the transition's precondition artifact) — add the comment, then retry",
			VerdictKind.InvalidOperation));
	}

	[Fact]
	public void GatedTransition_WithTheArtifactComment_Passes()
	{
		var ctx = IdeasCtx((Id("i1"), ["artifact:spec_plan"]));
		GuardEngine.RequirePreconditionArtifacts(ctx, [State("i1", "review", "idea")], Exploring).Should().BeNull();
	}

	[Fact]
	public void ArtifactTagMatching_IsCaseInsensitive()
	{
		var ctx = IdeasCtx((Id("i1"), ["ARTIFACT:Spec_Plan"]));
		GuardEngine.RequirePreconditionArtifacts(ctx, [State("i1", "review", "idea")], Exploring).Should().BeNull();
	}

	[Fact]
	public void AnUnrelatedCommentTag_DoesNotSatisfyTheGate()
	{
		var ctx = IdeasCtx((Id("i1"), ["artifact:something_else", "note"]));
		GuardEngine.RequirePreconditionArtifacts(ctx, [State("i1", "review", "idea")], Exploring)!.Node.Should().Be("i1");
	}

	[Fact]
	public void UngatedTransitions_AreNotJudged()
	{
		var prior = Prior(State("i1", "raw", "idea", nodeId: Id("i1")));
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "exploring", "idea")], prior).Should().BeNull();
	}

	[Fact]
	public void UnchangedStatus_IsNotATransition()
	{
		// Editing an idea that already sits in `review` must not re-litigate the gate it passed.
		var prior = Prior(State("i1", "review", "idea", nodeId: Id("i1")));
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "review", "idea")], prior).Should().BeNull();
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "REVIEW", "idea")], prior).Should().BeNull();
	}

	[Fact]
	public void BirthDirectlyIntoAGatedStatus_IsRefused_TheGateCantBeBypassed()
	{
		// No prior row at all: the node would be CREATED in `review`, so no transition fires — and
		// the gate would be skipped entirely if birth weren't checked separately.
		var v = GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "review", "idea")], NoPrior);
		v.Should().Be(new MethodologyVerdict("i1",
			"node 'i1' can't be created directly in 'review' — transition 'exploring' -> 'review' requires an artifact:spec_plan comment; create the node, add the comment, then transition",
			VerdictKind.InvalidOperation));
	}

	[Fact]
	public void BirthIntoAnUngatedStatus_IsFine() =>
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "raw", "idea")], NoPrior).Should().BeNull();

	[Fact]
	public void RecoveryFromAnUnknownStatus_IntoAGatedStatus_StillNeedsTheArtifact()
	{
		// The prior status isn't in this workflow (a legacy row), so from resolves to null exactly
		// like WorkflowEngine's recovery — but the node EXISTS, so it is judged against its tags,
		// not indicted for being "created directly".
		var prior = Prior(State("i1", "Pending", "idea", nodeId: Id("i1")));
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "review", "idea")], prior)!.Message
			.Should().Be("transition 'exploring' -> 'review' on node 'i1' requires an artifact:spec_plan comment (the transition's precondition artifact) — add the comment, then retry");

		var withTag = IdeasCtx((Id("i1"), ["artifact:spec_plan"]));
		GuardEngine.RequirePreconditionArtifacts(withTag, [State("i1", "review", "idea")], prior).Should().BeNull();
	}

	[Fact]
	public void APriorRowFoundThroughPrevKey_IsUsed()
	{
		// A renamed node keeps its history: the gate reads the source row's tags.
		var prior = Prior(State("old", "exploring", "idea", nodeId: Id("i1")));
		var ctx = IdeasCtx((Id("i1"), ["artifact:spec_plan"]));
		GuardEngine.RequirePreconditionArtifacts(ctx, [State("new", "review", "idea", prevKey: "old")], prior).Should().BeNull();
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("new", "review", "idea", prevKey: "old")], prior)!.Message
			.Should().Contain("on node 'new' requires an artifact:spec_plan comment");
	}

	[Fact]
	public void APriorRowWithoutANodeId_ReadsAsBirth()
	{
		var prior = Prior(State("i1", "exploring", "idea"));
		GuardEngine.RequirePreconditionArtifacts(IdeasCtx(), [State("i1", "review", "idea")], prior)!.Message
			.Should().StartWith("node 'i1' can't be created directly in 'review'");
	}

	[Fact]
	public void AKindWithNoGatedTransitions_IsNeverRefused()
	{
		var prior = Prior(State("t1", "Review", "feature", nodeId: Id("t1")));
		GuardEngine.RequirePreconditionArtifacts(Ctx(), [State("t1", "Done", "feature")], prior).Should().BeNull();
	}

	[Fact]
	public void AnUnknownType_IsSkipped_ApplyWorkflowAlreadyRejectedIt()
	{
		// For() returns null for an unresolvable (kind, type); this guard is not the one that
		// reports it.
		GuardEngine.RequirePreconditionArtifacts(Ctx(), [State("t1", "Pending")], NoPrior).Should().BeNull();
	}

	// ---- The artifacts-strictness switch (schema v2, spec methodology-gate-strictness) ----

	static readonly MethodologyRuntime SoftArtifactsRuntime = MethodologyRuntime.From(new MethodologyDefinition("soft-artifacts",
	[
		new MethodologyKindDef("gated", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["item"],
				[
					new WorkflowStatus("raw", "Raw", StatusKind.Open),
					new WorkflowStatus("reviewed", "Reviewed", StatusKind.Open),
				],
				[
					new MethodologyTransitionDef("raw", "reviewed")
					{
						RequiredArtifacts = [new RequiredArtifactDef("spec_plan")],
						Enforce = new GateEnforcementDef(Artifacts: false),
					},
				]),
		]),
	]));

	[Fact]
	public void EnforceArtifactsFalse_DemotesTheGateToConvention_NotBlocked()
	{
		var ctx = Ctx(runtime: SoftArtifactsRuntime, kindSlug: "gated", board: "gated-board", specBoard: null);
		var prior = Prior(State("g1", "raw", "item", nodeId: Id("g1")));
		// No artifact:spec_plan comment anywhere in this context, and yet:
		GuardEngine.RequirePreconditionArtifacts(ctx, [State("g1", "reviewed", "item")], prior).Should().BeNull();
	}
}
