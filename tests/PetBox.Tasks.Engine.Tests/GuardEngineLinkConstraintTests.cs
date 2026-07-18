using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// The two link guards, exercised through Decide (the resolvers collapsed into it): RequireDefinition
// Links ("who MUST carry which link, and when") and ValidateLinkTargets ("what a provided link may
// point at"). Both are DATA-driven off MethodologyRuntime.LinkConstraints, so the quartet's own
// rules (feature/bug need task_spec → a spec node; every spec write needs idea_spec → an accepted
// idea) are assertions ABOUT the preset data as much as about the engine.
public sealed class GuardEngineLinkConstraintTests
{
	static MethodologyVerdict? Decide(
		MethodologyEngineContext ctx, NodeState[] desired, Dictionary<string, NodeState>? prior = null,
		Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? links = null,
		Dictionary<string, string>? blockedBy = null)
	{
		var v = GuardEngine.Decide(ctx, desired, prior ?? NoPrior, links ?? NoLinks, blockedBy ?? NoRefs).Verdicts;
		return v.Count > 0 ? v[0] : null;
	}

	// ---- RequireDefinitionLinks: task_spec (structural — creation only) ----

	[Fact]
	public void NewFeature_WithoutTaskSpec_IsRefused()
	{
		Decide(Ctx(), [State("t1", "Pending", "feature")])!.Message
			.Should().Be("a new work feature must carry a task_spec link — provide links.task_spec — points at a `spec` node (node 't1')");
	}

	[Fact]
	public void NewBug_WithoutTaskSpec_IsRefused()
	{
		Decide(Ctx(), [State("t1", "Pending", "bug")])!.Message
			.Should().Be("a new work bug must carry a task_spec link — provide links.task_spec — points at a `spec` node (node 't1')");
	}

	[Fact]
	public void UntypedWorkNode_IsIndictedByItsEffectiveType()
	{
		// `work`'s default type is `feature` (declaration order), the type the constraint names.
		Decide(Ctx(), [State("t1", "Pending")])!.Message
			.Should().Be("a new work feature must carry a task_spec link — provide links.task_spec — points at a `spec` node (node 't1')");
	}

	[Fact]
	public void NewChore_NeedsNoTaskSpec()
	{
		// chore is exempt because NO constraint names it — the exemption is the absence of data.
		Decide(Ctx(), [State("t1", "Pending", "chore")]).Should().BeNull();
	}

	[Fact]
	public void NewFeature_WithTaskSpec_Passes()
	{
		var ctx = Ctx(index: [Node("s1", SpecBoardName, "spec", "auth", "defined", "spec")]);
		Decide(ctx, [State("t1", "Pending", "feature", nodeId: Id("t1"))], links: Link("t1", "task_spec", "auth")).Should().BeNull();
	}

	[Fact]
	public void EditingAnExistingFeature_DoesNotReRequireTaskSpec()
	{
		var prior = Prior(State("t1", "Pending", "feature", nodeId: Id("t1")));
		Decide(Ctx(), [State("t1", "InProgress", "feature")], prior).Should().BeNull();
	}

	[Fact]
	public void RenamedFeature_IsNotNew_WhenItsPrevKeyHasAPriorRow()
	{
		var prior = Prior(State("old-key", "Pending", "feature", nodeId: Id("t1")));
		Decide(Ctx(), [State("new-key", "Pending", "feature", prevKey: "old-key")], prior).Should().BeNull();
	}

	[Fact]
	public void RenamedFeature_WithAnUnknownPrevKey_IsStillNew()
	{
		Decide(Ctx(), [State("new-key", "Pending", "feature", prevKey: "ghost")], Prior())!.Message
			.Should().Be("a new work feature must carry a task_spec link — provide links.task_spec — points at a `spec` node (node 'new-key')");
	}

	// ---- RequireDefinitionLinks: idea_spec (provenance — EVERY write, because it pins a status) ----

	[Fact]
	public void NewSpecNode_WithoutIdeaSpec_IsRefused()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		Decide(ctx, [State("s1", "defined", "spec")])!.Message
			.Should().Be("every write of a spec must carry a idea_spec link — provide links.idea_spec — points at a `ideas` node in status accepted (node 's1')");
	}

	[Fact]
	public void EditingASpecNode_STILL_RequiresIdeaSpec()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		var prior = Prior(State("s1", "defined", "spec", nodeId: Id("s1")));
		Decide(ctx, [State("s1", "defined", "spec")], prior)!.Message
			.Should().StartWith("every write of a spec must carry a idea_spec link");
	}

	[Fact]
	public void UntypedSpecNode_ResolvesToTheKindsDefaultType_AndIsStillConstrained()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		Decide(ctx, [State("s1", "defined")])!.Message
			.Should().StartWith("every write of a spec must carry a idea_spec link");
	}

	[Fact]
	public void SpecNode_WithIdeaSpec_Passes()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "good", "accepted", "idea")]);
		Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "good")).Should().BeNull();
	}

	[Fact]
	public void FirstOffendingNode_InBatchOrder_IsTheOneIndicted()
	{
		Decide(Ctx(), [State("ok", "Pending", "chore"), State("bad1", "Pending", "feature"), State("bad2", "Pending", "bug")])!
			.Node.Should().Be("bad1");
	}

	[Fact]
	public void AKindWithNoConstraints_IsNeverRefused()
	{
		Decide(Ctx(kindSlug: "ideas", board: IdeasBoardName), [State("i1", "raw", "idea")]).Should().BeNull();
		Decide(Ctx(runtime: Presets, kindSlug: "simple", board: "misc"), [State("n1", "Todo", "task")]).Should().BeNull();
	}

	[Fact]
	public void BlocksConstraint_DemandsBlockedByAtCreation()
	{
		var def = new MethodologyDefinition("custom",
		[
			new MethodologyKindDef("gated", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["item"], [new WorkflowStatus("open", "Open", StatusKind.Open)], [])])
			{
				LinkConstraints = [new MethodologyLinkConstraintDef("item", "blocks")],
			},
		]);
		var ctx = Ctx(runtime: MethodologyRuntime.From(def), kindSlug: "gated", board: "g", specBoard: null);
		Decide(ctx, [State("g1", "open", "item")])!.Message
			.Should().Be("a new gated item must carry a blocks link — provide links.blocks (node 'g1')");

		Decide(ctx, [State("g1", "open", "item", nodeId: Id("g1"))], blockedBy: Refs(("g1", Id("b1")))).Should().BeNull();
	}

	[Fact]
	public void IdeaSpecConstraint_WithoutTargetStatuses_IsCreationOnly()
	{
		// A constraint that pins no target STATUS is structural (creation-only), not provenance.
		var def = new MethodologyDefinition("custom",
		[
			new MethodologyKindDef("doc", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["doc"], [new WorkflowStatus("draft", "Draft", StatusKind.Open)], [])])
			{
				LinkConstraints = [new MethodologyLinkConstraintDef("doc", "idea_spec") { TargetKind = "ideas" }],
			},
		]);
		var ctx = Ctx(runtime: MethodologyRuntime.From(def), kindSlug: "doc", board: "docs", specBoard: null);
		Decide(ctx, [State("d1", "draft", "doc")])!.Message
			.Should().Be("a new doc must carry a idea_spec link — provide links.idea_spec — points at a `ideas` node (node 'd1')");
	}

	// ---- ValidateLinkTargets (driven by a resolvable NodeId ref that violates the target rule) ----

	[Fact]
	public void NoLinks_IsANoOp() =>
		Decide(Ctx(), [State("t1", "Pending", "chore")]).Should().BeNull();

	[Fact]
	public void TaskSpec_ToAnUnknownNodeId_IsRefused()
	{
		var v = Decide(Ctx(), [State("t1", "Pending", "feature", nodeId: Id("t1"))], links: Link("t1", "task_spec", Id("ghost")));
		v!.Message.Should().Be($"links.task_spec '{Id("ghost")}' (node 't1') does not resolve to any node");
	}

	[Fact]
	public void TaskSpec_ToANonSpecBoard_IsRefused()
	{
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other-task", "Pending", "feature")]);
		Decide(ctx, [State("t1", "Pending", "feature", nodeId: Id("t1"))], links: Link("t1", "task_spec", Id("w2")))!.Message
			.Should().Be($"links.task_spec '{Id("w2")}' (node 't1') points to board 'work', which is not a spec board");
	}

	[Fact]
	public void TaskSpec_ToASpecNode_Passes()
	{
		var ctx = Ctx(index: [Node("s1", SpecBoardName, "spec", "auth", "defined", "spec")]);
		Decide(ctx, [State("t1", "Pending", "feature", nodeId: Id("t1"))], links: Link("t1", "task_spec", Id("s1"))).Should().BeNull();
	}

	[Fact]
	public void TaskSpec_TargetRule_AppliesToAVoluntaryRefToo()
	{
		// A chore isn't required to carry task_spec, but if it does, the same KIND-level target rule
		// validates it.
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other-task", "Pending", "chore")]);
		Decide(ctx, [State("chore1", "Pending", "chore", nodeId: Id("chore1"))], links: Link("chore1", "task_spec", Id("w2")))!.Message
			.Should().Contain("which is not a spec board");
	}

	[Fact]
	public void IdeaSpec_ToANonAcceptedIdea_IsRefused_NamingTheRequiredStatus()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "half-baked", "raw", "idea")]);
		Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", Id("i1")))!.Message
			.Should().Be($"links.idea_spec '{Id("i1")}' (node 's1') target is 'raw', not accepted — a spec change needs a ideas node in status accepted");
	}

	[Fact]
	public void IdeaSpec_ToAnAcceptedIdea_Passes()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "good", "accepted", "idea")]);
		Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", Id("i1"))).Should().BeNull();
	}

	[Fact]
	public void TaskSpec_OnTheWrongSpecBoard_TripsTheAutoWirePin()
	{
		var ctx = Ctx(index: [Node("s9", "spec-legacy", "spec", "auth", "defined", "spec")]);
		Decide(ctx, [State("t1", "Pending", "feature", nodeId: Id("t1"))], links: Link("t1", "task_spec", Id("s9")))!.Message
			.Should().Be($"links.task_spec '{Id("s9")}' (node 't1') is on board 'spec-legacy', but this board links spec board 'spec'");
	}

	[Fact]
	public void TaskSpec_WithNoSpecBoardPinned_SkipsThePin()
	{
		var ctx = Ctx(specBoard: null, index: [Node("s9", "spec-legacy", "spec", "auth", "defined", "spec")]);
		Decide(ctx, [State("t1", "Pending", "feature", nodeId: Id("t1"))], links: Link("t1", "task_spec", Id("s9"))).Should().BeNull();
	}

	[Fact]
	public void AKindWithNoTargetRule_OnlyChecksResolvability()
	{
		// `ideas` declares no link constraints, so an idea_spec link FROM an ideas node (ideas is the
		// FROM end of ideas→spec) is checked for existence and nothing more.
		var ctx = Ctx(kindSlug: "ideas", board: IdeasBoardName, specBoard: null,
			index: [Node("w1", WorkBoardName, "work", "task", "Pending", "feature")]);
		Decide(ctx, [State("i1", "raw", "idea", nodeId: Id("i1"))], links: Link("i1", "idea_spec", Id("w1"))).Should().BeNull();
	}
}
