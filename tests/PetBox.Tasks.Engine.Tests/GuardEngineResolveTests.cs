using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// The slug-or-NodeId resolution of specRef / blockedBy / ideaRef (acceptance condition 3: the
// resolvers are part of the DECISION, so their failures are verdicts with ForNode, not IO-side
// errors). Every negative assertion pins the EXACT message and VerdictKind, because the service
// re-raises those verbatim (ToRefusal → the exception type/message the partial-mode retry loop
// and the existing DB-bound suite already match on). Condition 5's "equality of error strings"
// is these assertions.
public sealed class GuardEngineResolveTests
{
	// ---- LooksLikeNodeId: the one definition both engine and NodeRefResolver key on ----

	[Theory]
	[InlineData("0123456789abcdef0123456789abcdef", true)]
	[InlineData("0123456789ABCDEF0123456789ABCDEF", true)]  // hex is case-insensitive
	[InlineData("0123456789abcdef0123456789abcde", false)]  // 31 chars
	[InlineData("0123456789abcdef0123456789abcdef0", false)] // 33 chars
	[InlineData("0123456789abcdef0123456789abcdeg", false)] // 32 chars, not hex
	[InlineData("my-spec-node", false)]
	[InlineData("", false)]
	public void LooksLikeNodeId_IsLength32AndHex(string value, bool expected) =>
		GuardEngine.LooksLikeNodeId(value).Should().Be(expected);

	// ---- ResolveSpecRefs ----

	[Fact]
	public void ResolveSpecRefs_Empty_IsNoOp()
	{
		var v = GuardEngine.ResolveSpecRefs(Ctx(), NoRefs, out var resolved);
		v.Should().BeNull();
		resolved.Should().BeEmpty();
	}

	[Fact]
	public void ResolveSpecRefs_Slug_ResolvesOnLinkedSpecBoard()
	{
		var ctx = Ctx(index: [Node("spec1", SpecBoardName, "spec", "auth-tokens", "defined", "spec")]);
		var v = GuardEngine.ResolveSpecRefs(ctx, Refs(("t1", "auth-tokens")), out var resolved);
		v.Should().BeNull();
		resolved["t1"].Should().Be(Id("spec1"));
	}

	[Fact]
	public void ResolveSpecRefs_Slug_IsCaseInsensitiveAndTrimmed()
	{
		var ctx = Ctx(index: [Node("spec1", SpecBoardName, "spec", "auth-tokens", "defined", "spec")]);
		var v = GuardEngine.ResolveSpecRefs(ctx, Refs(("t1", "  AUTH-Tokens  ")), out var resolved);
		v.Should().BeNull();
		resolved["t1"].Should().Be(Id("spec1"));
	}

	[Fact]
	public void ResolveSpecRefs_NodeId_PassesThroughUntouched()
	{
		// A NodeId-shaped value is NOT looked up here — resolution only turns slugs into ids;
		// whether the id exists is ValidateLinkTargets' question.
		var ctx = Ctx();
		var id = Id("deadbeef");
		var v = GuardEngine.ResolveSpecRefs(ctx, Refs(("t1", $" {id} ")), out var resolved);
		v.Should().BeNull();
		resolved["t1"].Should().Be(id);
	}

	[Fact]
	public void ResolveSpecRefs_Slug_WithoutLinkedSpecBoard_IsRefused()
	{
		var ctx = Ctx(specBoard: null);
		var v = GuardEngine.ResolveSpecRefs(ctx, Refs(("t1", "auth-tokens")), out _);
		v.Should().Be(new MethodologyVerdict("t1",
			"specRef 'auth-tokens' (node 't1') is a slug, but this board has no linked spec board — provide the spec node's NodeId",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void ResolveSpecRefs_Slug_NoMatchOnSpecBoard_IsRefused()
	{
		// The node exists — on the WRONG board. specRef slugs resolve only against the linked
		// spec board, so this is a miss, not a cross-board hit.
		var ctx = Ctx(index: [Node("w1", WorkBoardName, "work", "auth-tokens", "Pending", "feature")]);
		var v = GuardEngine.ResolveSpecRefs(ctx, Refs(("t1", "auth-tokens")), out _);
		v.Should().Be(new MethodologyVerdict("t1",
			"specRef 'auth-tokens' (node 't1') does not match any node on spec board 'spec'",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void ResolveSpecRefs_ErrorNamesTheRawValue_NotTheTrimmedOne()
	{
		var ctx = Ctx(specBoard: null);
		var v = GuardEngine.ResolveSpecRefs(ctx, Refs(("t1", " Nope ")), out _);
		v!.Message.Should().StartWith("specRef ' Nope ' (node 't1')");
	}

	// ---- ResolveBlockedBy ----

	[Fact]
	public void ResolveBlockedBy_Slug_ResolvesAgainstPriorRowsOnThisBoard()
	{
		var prior = Prior(State("blocker", "InProgress", "feature", nodeId: Id("b1")));
		var v = GuardEngine.ResolveBlockedBy(Ctx(), [], prior, Refs(("t1", "blocker")), out var resolved);
		v.Should().BeNull();
		resolved["t1"].Should().Be(Id("b1"));
	}

	[Fact]
	public void ResolveBlockedBy_Slug_ResolvesAgainstANodeBornInTheSameBatch()
	{
		// The desired overlay is what makes "create the blocker and the blocked task in one call"
		// work: the blocker has no prior row, only a desired one carrying its fresh NodeId.
		var desired = new[] { State("blocker", "Pending", "feature", nodeId: Id("b2")) };
		var v = GuardEngine.ResolveBlockedBy(Ctx(), desired, NoPrior, Refs(("t1", "blocker")), out var resolved);
		v.Should().BeNull();
		resolved["t1"].Should().Be(Id("b2"));
	}

	[Fact]
	public void ResolveBlockedBy_DesiredOverlay_WinsOverPrior()
	{
		var prior = Prior(State("blocker", "InProgress", "feature", nodeId: Id("old")));
		var desired = new[] { State("blocker", "InProgress", "feature", nodeId: Id("new")) };
		GuardEngine.ResolveBlockedBy(Ctx(), desired, prior, Refs(("t1", "blocker")), out var resolved).Should().BeNull();
		resolved["t1"].Should().Be(Id("new"));
	}

	[Fact]
	public void ResolveBlockedBy_RowsWithoutANodeId_AreNotResolutionCandidates()
	{
		// A desired row's NodeId is assigned inside the temporal upsert; an empty one can't be a
		// blocker target (the map would otherwise bind a slug to "").
		var desired = new[] { State("blocker", "Pending", "feature") };
		var v = GuardEngine.ResolveBlockedBy(Ctx(), desired, NoPrior, Refs(("t1", "blocker")), out _);
		v!.Kind.Should().Be(VerdictKind.InvalidArgument);
	}

	[Fact]
	public void ResolveBlockedBy_NodeId_PassesThroughUntouched()
	{
		var id = Id("cafe");
		GuardEngine.ResolveBlockedBy(Ctx(), [], NoPrior, Refs(("t1", id)), out var resolved).Should().BeNull();
		resolved["t1"].Should().Be(id);
	}

	[Fact]
	public void ResolveBlockedBy_UnknownSlug_IsRefused_NamingTheBoard()
	{
		var v = GuardEngine.ResolveBlockedBy(Ctx(), [], NoPrior, Refs(("t1", "ghost")), out _);
		v.Should().Be(new MethodologyVerdict("t1",
			"blockedBy 'ghost' (node 't1') does not match any node on board 'work' — a blocker's slug resolves on the same board; pass a NodeId to reference a node on another board",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void ResolveBlockedBy_Empty_IsNoOp()
	{
		GuardEngine.ResolveBlockedBy(Ctx(), [], NoPrior, NoRefs, out var resolved).Should().BeNull();
		resolved.Should().BeEmpty();
	}

	// ---- ResolveIdeaRefs ----

	[Fact]
	public void ResolveIdeaRefs_Slug_ResolvesOnTheInstancesIdeaBoard()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "faster-search", "accepted", "idea")]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "faster-search")), out var resolved);
		v.Should().BeNull();
		resolved["s1"].Should().Be(Id("i1"));
	}

	[Fact]
	public void ResolveIdeaRefs_AnyActiveStatusResolves_TheStatusRuleRunsLater()
	{
		// Resolution is status-blind on purpose: a `raw` idea RESOLVES, and ValidateLinkTargets is
		// what then refuses it for not being `accepted`. Two rules, two messages.
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "half-baked", "raw", "idea")]);
		GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "half-baked")), out var resolved).Should().BeNull();
		resolved["s1"].Should().Be(Id("i1"));
	}

	[Fact]
	public void ResolveIdeaRefs_NodeId_PassesThroughTrimmed()
	{
		var id = Id("beef");
		GuardEngine.ResolveIdeaRefs(Ctx(kindSlug: "spec"), Refs(("s1", $" {id} ")), out var resolved).Should().BeNull();
		resolved["s1"].Should().Be(id);
	}

	[Fact]
	public void ResolveIdeaRefs_NoIdeaBoardInTheInstance_IsRefused()
	{
		// The ideas board exists — in ANOTHER methodology instance. Instance membership is the
		// bucket, so this project's spec board sees no idea board at all.
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			boards:
			[
				new EngineBoard(SpecBoardName, "spec", Instance, Closed: false),
				new EngineBoard(IdeasBoardName, "ideas", "other-instance", Closed: false),
			]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "faster-search")), out _);
		v.Should().Be(new MethodologyVerdict("s1",
			"ideaRef 'faster-search' (node 's1') is a slug, but no active ideas board exists alongside board 'spec' in methodology instance 'inst-1' — create one or provide the idea node's NodeId",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void ResolveIdeaRefs_ClosedIdeaBoard_DoesNotCount()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			boards:
			[
				new EngineBoard(SpecBoardName, "spec", Instance, Closed: false),
				new EngineBoard(IdeasBoardName, "ideas", Instance, Closed: true),
			]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "faster-search")), out _);
		v!.Message.Should().Contain("no active ideas board exists alongside board 'spec'");
	}

	[Fact]
	public void ResolveIdeaRefs_NoInstance_OmitsTheInstanceSuffix()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName, instance: "",
			boards: [new EngineBoard(SpecBoardName, "spec", "", Closed: false)]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "faster-search")), out _);
		v!.Message.Should().Be(
			"ideaRef 'faster-search' (node 's1') is a slug, but no active ideas board exists alongside board 'spec' — create one or provide the idea node's NodeId");
	}

	[Fact]
	public void ResolveIdeaRefs_NoMatch_IsRefused_NamingTheSingleBoard()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "something-else", "accepted", "idea")]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "ghost")), out _);
		v.Should().Be(new MethodologyVerdict("s1",
			"ideaRef 'ghost' (node 's1') does not match any node on ideas board 'ideas'",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void ResolveIdeaRefs_NoMatch_AcrossSeveralIdeaBoards_PluralizesAndListsThem()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			boards:
			[
				new EngineBoard(SpecBoardName, "spec", Instance, Closed: false),
				new EngineBoard("ideas-b", "ideas", Instance, Closed: false),
				new EngineBoard("ideas-a", "ideas", Instance, Closed: false),
			]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "ghost")), out _);
		// Ordinal-sorted board list, "board" pluralized.
		v!.Message.Should().Be("ideaRef 'ghost' (node 's1') does not match any node on ideas boards 'ideas-a, ideas-b'");
	}

	[Fact]
	public void ResolveIdeaRefs_SameSlugOnTwoIdeaBoards_IsAmbiguous()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index:
			[
				Node("i1", "ideas-b", "ideas", "faster-search", "accepted", "idea"),
				Node("i2", "ideas-a", "ideas", "faster-search", "accepted", "idea"),
			],
			boards:
			[
				new EngineBoard(SpecBoardName, "spec", Instance, Closed: false),
				new EngineBoard("ideas-b", "ideas", Instance, Closed: false),
				new EngineBoard("ideas-a", "ideas", Instance, Closed: false),
			]);
		var v = GuardEngine.ResolveIdeaRefs(ctx, Refs(("s1", "faster-search")), out _);
		v.Should().Be(new MethodologyVerdict("s1",
			"ambiguous ideaRef 'faster-search' (node 's1') — the slug matches nodes on boards: [ideas-a, ideas-b]; pass the idea node's NodeId instead",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void ResolveIdeaRefs_TargetKindComesFromTheKindsIdeaSpecConstraint()
	{
		// The target kind is DATA, not the literal `ideas`: a definition whose idea_spec
		// constraint names another kind resolves slugs against THAT kind's boards.
		var def = new MethodologyDefinition("custom",
		[
			new MethodologyKindDef("doc", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["doc"], [new WorkflowStatus("draft", "Draft", StatusKind.Open)], [])])
			{
				LinkConstraints = [new MethodologyLinkConstraintDef("doc", "idea_spec") { TargetKind = "rfc" }],
			},
			new MethodologyKindDef("rfc", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["rfc"], [new WorkflowStatus("open", "Open", StatusKind.Open)], [])]),
		]);
		var ctx = Ctx(runtime: MethodologyRuntime.From(def), kindSlug: "doc", board: "docs",
			index: [Node("r1", "rfcs", "rfc", "streaming", "open", "rfc")],
			boards:
			[
				new EngineBoard("docs", "doc", Instance, Closed: false),
				new EngineBoard("rfcs", "rfc", Instance, Closed: false),
			]);
		GuardEngine.ResolveIdeaRefs(ctx, Refs(("d1", "streaming")), out var resolved).Should().BeNull();
		resolved["d1"].Should().Be(Id("r1"));

		// ...and the miss message names that declared kind, not `ideas`.
		GuardEngine.ResolveIdeaRefs(ctx, Refs(("d1", "ghost")), out _)!.Message
			.Should().Be("ideaRef 'ghost' (node 'd1') does not match any node on rfc board 'rfcs'");
	}
}
