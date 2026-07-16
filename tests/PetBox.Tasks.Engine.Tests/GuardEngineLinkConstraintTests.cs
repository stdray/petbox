using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// The two link guards, as pure functions: RequireDefinitionLinks ("who MUST carry which link, and
// when") and ValidateLinkTargets ("what a provided link may point at"). Both are DATA-driven off
// MethodologyRuntime.LinkConstraints, so the quartet's own rules (feature/bug need a specRef to a
// spec node; every spec write needs an ideaRef to an accepted idea) are assertions ABOUT the
// preset data as much as about the engine. Messages are pinned exactly — condition 5.
public sealed class GuardEngineLinkConstraintTests
{
	static MethodologyVerdict? Require(
		MethodologyEngineContext ctx, NodeState[] desired, Dictionary<string, NodeState>? prior = null,
		Dictionary<string, string>? specRefs = null, Dictionary<string, string>? blockedBy = null,
		Dictionary<string, string>? ideaRefs = null) =>
		GuardEngine.RequireDefinitionLinks(ctx, desired, prior ?? [], specRefs ?? NoRefs, blockedBy ?? NoRefs, ideaRefs ?? NoRefs);

	// ---- RequireDefinitionLinks: task_spec (structural — creation only) ----

	[Fact]
	public void NewFeature_WithoutSpecRef_IsRefused()
	{
		var v = Require(Ctx(), [State("t1", "Pending", "feature")]);
		v.Should().Be(new MethodologyVerdict("t1",
			"a work feature must link a spec node — provide specRef (node 't1')",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void NewBug_WithoutSpecRef_IsRefused()
	{
		var v = Require(Ctx(), [State("t1", "Pending", "bug")]);
		v!.Message.Should().Be("a work bug must link a spec node — provide specRef (node 't1')");
	}

	[Fact]
	public void UntypedWorkNode_IsIndictedByItsEFFECTIVEType_NotABlankOne()
	{
		// The message must name the type the constraint MATCHED on. `work`'s default type is
		// `feature` (declaration order), and `feature` is exactly what the task_spec constraint
		// names — so an untyped work node is refused BY the feature rule and must be told so.
		// Interpolating the node's RAW type here would read "a work  must link..." (double
		// space): a verdict that matched on `feature` but refuses to say the word.
		Require(Ctx(), [State("t1", "Pending")])!.Message
			.Should().Be("a work feature must link a spec node — provide specRef (node 't1')");
	}

	[Fact]
	public void NewChore_NeedsNoSpecRef()
	{
		// chore is exempt because NO constraint names it — the exemption is the absence of data,
		// not a hardcoded `if`.
		Require(Ctx(), [State("t1", "Pending", "chore")]).Should().BeNull();
	}

	[Fact]
	public void NewFeature_WithSpecRef_Passes() =>
		Require(Ctx(), [State("t1", "Pending", "feature")], specRefs: Refs(("t1", Id("s1")))).Should().BeNull();

	[Fact]
	public void EditingAnExistingFeature_DoesNotReRequireSpecRef()
	{
		// task_spec is STRUCTURAL: required at creation, never re-demanded on an edit.
		var prior = Prior(State("t1", "Pending", "feature", nodeId: Id("t1")));
		Require(Ctx(), [State("t1", "InProgress", "feature")], prior).Should().BeNull();
	}

	[Fact]
	public void RenamedFeature_IsNotNew_WhenItsPrevKeyHasAPriorRow()
	{
		// A rename carries PrevKey; the node is the same node, so it isn't being created.
		var prior = Prior(State("old-key", "Pending", "feature", nodeId: Id("t1")));
		Require(Ctx(), [State("new-key", "Pending", "feature", prevKey: "old-key")], prior).Should().BeNull();
	}

	[Fact]
	public void RenamedFeature_WithAnUnknownPrevKey_IsStillNew()
	{
		var v = Require(Ctx(), [State("new-key", "Pending", "feature", prevKey: "ghost")], Prior());
		v!.Message.Should().Be("a work feature must link a spec node — provide specRef (node 'new-key')");
	}

	// ---- RequireDefinitionLinks: idea_spec (provenance — EVERY write) ----

	[Fact]
	public void NewSpecNode_WithoutIdeaRef_IsRefused()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		var v = Require(ctx, [State("s1", "defined", "spec")]);
		v.Should().Be(new MethodologyVerdict("s1",
			"a spec change must be made under accepted ideas — provide ideaRef (node 's1')",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void EditingASpecNode_STILL_RequiresIdeaRef()
	{
		// The cadence difference that matters: unlike task_spec, a provenance link is demanded on
		// every write — each spec change names the idea that authorizes it.
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		var prior = Prior(State("s1", "defined", "spec", nodeId: Id("s1")));
		var v = Require(ctx, [State("s1", "defined", "spec")], prior);
		v!.Message.Should().Be("a spec change must be made under accepted ideas — provide ideaRef (node 's1')");
	}

	[Fact]
	public void UntypedSpecNode_ResolvesToTheKindsDefaultType_AndIsStillConstrained()
	{
		// An untyped node on a single-FSM kind IS that kind's default type; the constraint must
		// reach it (the old kind-wide guard's reach, now expressed as data).
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		Require(ctx, [State("s1", "defined")])!.Message
			.Should().Be("a spec change must be made under accepted ideas — provide ideaRef (node 's1')");
	}

	[Fact]
	public void SpecNode_WithIdeaRef_Passes()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		Require(ctx, [State("s1", "defined", "spec")], ideaRefs: Refs(("s1", Id("i1")))).Should().BeNull();
	}

	[Fact]
	public void TypeMatching_IsCaseInsensitive() =>
		Require(Ctx(), [State("t1", "Pending", "FEATURE")])!.Node.Should().Be("t1");

	[Fact]
	public void AKindWithNoConstraints_IsNeverRefused()
	{
		// ideas/intake/simple declare none — the guard exits before touching a node.
		Require(Ctx(kindSlug: "ideas", board: IdeasBoardName), [State("i1", "raw", "idea")]).Should().BeNull();
		Require(Ctx(runtime: Presets, kindSlug: "simple", board: "misc"), [State("n1", "Todo", "task")]).Should().BeNull();
	}

	[Fact]
	public void FirstOffendingNode_InBatchOrder_IsTheOneIndicted()
	{
		// Fail-fast, and the batch is walked in order: the verdict names the FIRST violator.
		var v = Require(Ctx(), [State("ok", "Pending", "chore"), State("bad1", "Pending", "feature"), State("bad2", "Pending", "bug")]);
		v!.Node.Should().Be("bad1");
	}

	[Fact]
	public void BlocksConstraint_DemandsBlockedByAtCreation()
	{
		// The third expressible link kind (no preset declares it, a definition may).
		var def = new MethodologyDefinition("custom",
		[
			new MethodologyKindDef("gated", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["item"], [new WorkflowStatus("open", "Open", StatusKind.Open)], [])])
			{
				LinkConstraints = [new MethodologyLinkConstraintDef("item", "blocks")],
			},
		]);
		var ctx = Ctx(runtime: MethodologyRuntime.From(def), kindSlug: "gated", board: "g");
		var v = Require(ctx, [State("g1", "open", "item")]);
		v.Should().Be(new MethodologyVerdict("g1",
			"a gated item must carry a blocks link at creation — provide blockedBy (node 'g1')",
			VerdictKind.InvalidArgument));

		Require(ctx, [State("g1", "open", "item")], blockedBy: Refs(("g1", Id("b1")))).Should().BeNull();
	}

	[Fact]
	public void IdeaSpecConstraint_WithoutTargetStatuses_UsesTheGenericWording()
	{
		var def = new MethodologyDefinition("custom",
		[
			new MethodologyKindDef("doc", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["doc"], [new WorkflowStatus("draft", "Draft", StatusKind.Open)], [])])
			{
				LinkConstraints = [new MethodologyLinkConstraintDef("doc", "idea_spec")],
			},
		]);
		var ctx = Ctx(runtime: MethodologyRuntime.From(def), kindSlug: "doc", board: "docs");
		Require(ctx, [State("d1", "draft", "doc")])!.Message
			.Should().Be("a doc doc must carry a idea_spec link on every write — provide ideaRef (node 'd1')");
	}

	// ---- ValidateLinkTargets ----

	[Fact]
	public void NoRefs_IsANoOp() =>
		GuardEngine.ValidateLinkTargets(Ctx(), NoRefs, NoRefs).Should().BeNull();

	[Fact]
	public void SpecRef_ToAnUnknownNodeId_IsRefused()
	{
		var v = GuardEngine.ValidateLinkTargets(Ctx(), Refs(("t1", Id("ghost"))), NoRefs);
		v.Should().Be(new MethodologyVerdict("t1",
			$"specRef '{Id("ghost")}' (node 't1') does not resolve to any node",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void IdeaRef_ToAnUnknownNodeId_IsRefused_NamingItsOwnField()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName);
		var v = GuardEngine.ValidateLinkTargets(ctx, NoRefs, Refs(("s1", Id("ghost"))));
		v!.Message.Should().Be($"ideaRef '{Id("ghost")}' (node 's1') does not resolve to any node");
	}

	[Fact]
	public void SpecRef_ToANonSpecBoard_IsRefused()
	{
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other-task", "Pending", "feature")]);
		var v = GuardEngine.ValidateLinkTargets(ctx, Refs(("t1", Id("w2"))), NoRefs);
		v.Should().Be(new MethodologyVerdict("t1",
			$"specRef '{Id("w2")}' (node 't1') points to board 'work', which is not a spec board",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void SpecRef_ToASpecNode_Passes()
	{
		var ctx = Ctx(index: [Node("s1", SpecBoardName, "spec", "auth", "defined", "spec")]);
		GuardEngine.ValidateLinkTargets(ctx, Refs(("t1", Id("s1"))), NoRefs).Should().BeNull();
	}

	[Fact]
	public void SpecRef_TargetRule_AppliesToAVoluntaryRefToo()
	{
		// The target rule is KIND-level: the constraint's Type says who MUST carry the link, but a
		// chore that voluntarily provides a specRef is validated by the same rule.
		var ctx = Ctx(index: [Node("w2", WorkBoardName, "work", "other-task", "Pending", "chore")]);
		GuardEngine.ValidateLinkTargets(ctx, Refs(("chore1", Id("w2"))), NoRefs)!.Message
			.Should().Contain("which is not a spec board");
	}

	[Fact]
	public void IdeaRef_ToANonAcceptedIdea_IsRefused_NamingTheRequiredStatus()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "half-baked", "raw", "idea")]);
		var v = GuardEngine.ValidateLinkTargets(ctx, NoRefs, Refs(("s1", Id("i1"))));
		v.Should().Be(new MethodologyVerdict("s1",
			$"ideaRef '{Id("i1")}' (node 's1') target is 'raw', not accepted — a spec change needs a ideas node in status accepted",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void IdeaRef_ToAnAcceptedIdea_Passes()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "good", "accepted", "idea")]);
		GuardEngine.ValidateLinkTargets(ctx, NoRefs, Refs(("s1", Id("i1")))).Should().BeNull();
	}

	[Fact]
	public void IdeaRef_ToANodeOnAWorkBoard_FailsTheKindRuleFirst()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("w1", WorkBoardName, "work", "task", "Pending", "feature")]);
		GuardEngine.ValidateLinkTargets(ctx, NoRefs, Refs(("s1", Id("w1"))))!.Message
			.Should().Be($"ideaRef '{Id("w1")}' (node 's1') points to board 'work', which is not a ideas board");
	}

	[Fact]
	public void SpecRef_OnTheWrongSpecBoard_TripsTheAutoWirePin()
	{
		// The target IS a spec-kind node, so the data rule passes — but this work board pins a
		// specific spec board, and the pin is board meta, not methodology.
		var ctx = Ctx(index: [Node("s9", "spec-legacy", "spec", "auth", "defined", "spec")]);
		var v = GuardEngine.ValidateLinkTargets(ctx, Refs(("t1", Id("s9"))), NoRefs);
		v.Should().Be(new MethodologyVerdict("t1",
			$"specRef '{Id("s9")}' (node 't1') is on board 'spec-legacy', but this work board links spec board 'spec'",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void SpecRef_WithNoSpecBoardPinned_SkipsThePin()
	{
		var ctx = Ctx(specBoard: null, index: [Node("s9", "spec-legacy", "spec", "auth", "defined", "spec")]);
		GuardEngine.ValidateLinkTargets(ctx, Refs(("t1", Id("s9"))), NoRefs).Should().BeNull();
	}

	[Fact]
	public void AKindWithNoTargetRule_OnlyChecksResolvability()
	{
		// `ideas` declares no link constraints, so a specRef from an ideas board is checked for
		// existence and nothing more.
		var ctx = Ctx(kindSlug: "ideas", board: IdeasBoardName, specBoard: null,
			index: [Node("w1", WorkBoardName, "work", "task", "Pending", "feature")]);
		GuardEngine.ValidateLinkTargets(ctx, Refs(("i1", Id("w1"))), NoRefs).Should().BeNull();
	}
}
