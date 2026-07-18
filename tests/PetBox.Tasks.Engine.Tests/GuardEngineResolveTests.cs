using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// The slug-or-NodeId resolution of the generic links door + the blockedBy sugar (acceptance
// condition 3: the resolver is part of the DECISION, so its failures are verdicts with ForNode,
// not IO-side errors). Every negative assertion pins the message shape, because the service
// re-raises those verbatim (ToRefusal → the exception the partial-mode retry loop matches on).
// Resolution is exercised through GuardEngine.Decide, the only public seam now that the per-sugar
// resolvers collapsed into one generic ResolveLinks.
public sealed class GuardEngineResolveTests
{
	static MethodologyEngineDecision Decide(
		MethodologyEngineContext ctx, NodeState[] desired, Dictionary<string, NodeState>? prior = null,
		Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? links = null,
		Dictionary<string, string>? blockedBy = null) =>
		GuardEngine.Decide(ctx, desired, prior ?? NoPrior, links ?? NoLinks, blockedBy ?? NoRefs);

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

	// ---- task_spec (work→spec) via links door ----

	[Fact]
	public void TaskSpec_Slug_ResolvesOnTheInstanceSpecBoard()
	{
		var ctx = Ctx(index: [Node("spec1", SpecBoardName, "spec", "auth-tokens", "defined", "spec")]);
		var d = Decide(ctx, [State("t1", "Pending", "chore", nodeId: Id("t1"))], links: Link("t1", "task_spec", "auth-tokens"));
		d.Verdicts.Should().BeEmpty();
		var link = d.Links.Single(l => l.Kind == "task_spec");
		link.WriterKey.Should().Be("t1");
		link.TargetNodeId.Should().Be(Id("spec1"));
		link.WriterIsFrom.Should().BeTrue(); // work is the FROM end of work→spec
	}

	[Fact]
	public void TaskSpec_Slug_IsCaseInsensitiveAndTrimmed()
	{
		var ctx = Ctx(index: [Node("spec1", SpecBoardName, "spec", "auth-tokens", "defined", "spec")]);
		var d = Decide(ctx, [State("t1", "Pending", "chore", nodeId: Id("t1"))], links: Link("t1", "task_spec", "  AUTH-Tokens  "));
		d.Verdicts.Should().BeEmpty();
		d.Links.Single(l => l.Kind == "task_spec").TargetNodeId.Should().Be(Id("spec1"));
	}

	[Fact]
	public void TaskSpec_NodeId_PassesThroughUntouched()
	{
		// A NodeId-shaped value is not looked up here — resolution only turns slugs into ids; whether
		// the id exists is ValidateLinkTargets' question, so a bare NodeId on a chore (no constraint)
		// resolves clean.
		var id = Id("deadbeef");
		var ctx = Ctx(index: [Node("deadbeef", SpecBoardName, "spec", "x", "defined", "spec")]);
		var d = Decide(ctx, [State("t1", "Pending", "chore", nodeId: Id("t1"))], links: Link("t1", "task_spec", $" {id} "));
		d.Verdicts.Should().BeEmpty();
		d.Links.Single(l => l.Kind == "task_spec").TargetNodeId.Should().Be(id);
	}

	[Fact]
	public void TaskSpec_Slug_NoMatchOnSpecBoard_IsRefused()
	{
		var ctx = Ctx(index: [Node("w1", WorkBoardName, "work", "auth-tokens", "Pending", "feature")]);
		var d = Decide(ctx, [State("t1", "Pending", "chore", nodeId: Id("t1"))], links: Link("t1", "task_spec", "auth-tokens"));
		var v = d.Verdicts.Single();
		v.Message.Should().Be("links.task_spec 'auth-tokens' (node 't1') does not match any node on spec board 'spec'");
		v.Kind.Should().Be(VerdictKind.InvalidArgument);
	}

	[Fact]
	public void TaskSpec_ErrorNamesTheRawValue_NotTheTrimmedOne()
	{
		var ctx = Ctx(index: [Node("spec1", SpecBoardName, "spec", "auth", "defined", "spec")]);
		var d = Decide(ctx, [State("t1", "Pending", "chore", nodeId: Id("t1"))], links: Link("t1", "task_spec", " Nope "));
		d.Verdicts.Single().Message.Should().StartWith("links.task_spec ' Nope ' (node 't1')");
	}

	// ---- blockedBy (blocks) sugar — same-board resolution ----

	[Fact]
	public void BlockedBy_Slug_ResolvesAgainstPriorRowsOnThisBoard()
	{
		var prior = Prior(State("blocker", "InProgress", "feature", nodeId: Id("b1")));
		var d = Decide(Ctx(), [], prior, blockedBy: Refs(("t1", "blocker")));
		d.Verdicts.Should().BeEmpty();
		var link = d.Links.Single(l => l.Kind == "blocks");
		link.WriterKey.Should().Be("t1");
		link.TargetNodeId.Should().Be(Id("b1"));
		link.WriterIsFrom.Should().BeFalse(); // blocker→task, the task is the TO end
	}

	[Fact]
	public void BlockedBy_Slug_ResolvesAgainstANodeBornInTheSameBatch()
	{
		var desired = new[] { State("blocker", "Pending", "chore", nodeId: Id("b2")) };
		var d = Decide(Ctx(), desired, NoPrior, blockedBy: Refs(("t1", "blocker")));
		d.Verdicts.Should().BeEmpty();
		d.Links.Single(l => l.Kind == "blocks").TargetNodeId.Should().Be(Id("b2"));
	}

	[Fact]
	public void BlockedBy_NodeId_PassesThroughUntouched()
	{
		var id = Id("cafe");
		var d = Decide(Ctx(), [], NoPrior, blockedBy: Refs(("t1", id)));
		d.Verdicts.Should().BeEmpty();
		d.Links.Single(l => l.Kind == "blocks").TargetNodeId.Should().Be(id);
	}

	[Fact]
	public void BlockedBy_UnknownSlug_IsRefused_NamingTheBoard()
	{
		var d = Decide(Ctx(), [], NoPrior, blockedBy: Refs(("t1", "ghost")));
		d.Verdicts.Single().Message.Should().Be(
			"blockedBy 'ghost' (node 't1') does not match any node on board 'work' — a blocker's slug resolves on the same board; pass a NodeId to reference a node on another board");
	}

	[Fact]
	public void Blocks_SetTwoWays_IsRefused()
	{
		// blockedBy sugar AND links.blocks on one node — one link, one way.
		var prior = Prior(State("blocker", "InProgress", "feature", nodeId: Id("b1")));
		var d = Decide(Ctx(), [], prior, links: Link("t1", "blocks", Id("b1")), blockedBy: Refs(("t1", "blocker")));
		d.Verdicts.Single().Message.Should().Contain("sets the `blocks` link two ways");
	}

	// ---- idea_spec (ideas→spec) via links door, from a spec writer ----

	[Fact]
	public void IdeaSpec_Slug_ResolvesOnTheInstancesIdeaBoard()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "faster-search", "accepted", "idea")]);
		var d = Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "faster-search"));
		d.Verdicts.Should().BeEmpty();
		var link = d.Links.Single(l => l.Kind == "idea_spec");
		link.TargetNodeId.Should().Be(Id("i1"));
		link.WriterIsFrom.Should().BeFalse(); // spec is the TO end of ideas→spec, so idea→spec
	}

	[Fact]
	public void IdeaSpec_AnyActiveStatusResolves_TheStatusRuleRunsLater()
	{
		// Resolution is status-blind on purpose: a `raw` idea RESOLVES, and ValidateLinkTargets is
		// what then refuses it for not being `accepted`. Two rules, two messages.
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			index: [Node("i1", IdeasBoardName, "ideas", "half-baked", "raw", "idea")]);
		var d = Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "half-baked"));
		// The target-status rule speaks (raw ≠ accepted), but the RESOLUTION found the node.
		d.Verdicts.Single().Message.Should().Contain("target is 'raw', not accepted");
	}

	[Fact]
	public void IdeaSpec_NoIdeaBoardInTheInstance_IsRefused()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			boards:
			[
				new EngineBoard(SpecBoardName, "spec", Instance, Closed: false),
				new EngineBoard(IdeasBoardName, "ideas", "other-instance", Closed: false),
			]);
		var d = Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "faster-search"));
		d.Verdicts.Single().Message.Should().Be(
			"links.idea_spec 'faster-search' (node 's1') is a slug, but no active ideas board exists alongside board 'spec' in methodology instance 'inst-1' — create one or provide the target node's NodeId");
	}

	[Fact]
	public void IdeaSpec_NoInstance_OmitsTheInstanceSuffix()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName, instance: "",
			boards: [new EngineBoard(SpecBoardName, "spec", "", Closed: false)]);
		var d = Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "faster-search"));
		d.Verdicts.Single().Message.Should().Be(
			"links.idea_spec 'faster-search' (node 's1') is a slug, but no active ideas board exists alongside board 'spec' — create one or provide the target node's NodeId");
	}

	[Fact]
	public void IdeaSpec_NoMatch_AcrossSeveralIdeaBoards_PluralizesAndListsThem()
	{
		var ctx = Ctx(kindSlug: "spec", board: SpecBoardName,
			boards:
			[
				new EngineBoard(SpecBoardName, "spec", Instance, Closed: false),
				new EngineBoard("ideas-b", "ideas", Instance, Closed: false),
				new EngineBoard("ideas-a", "ideas", Instance, Closed: false),
			]);
		var d = Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "ghost"));
		d.Verdicts.Single().Message.Should().Be("links.idea_spec 'ghost' (node 's1') does not match any node on ideas boards 'ideas-a, ideas-b'");
	}

	[Fact]
	public void IdeaSpec_SameSlugOnTwoIdeaBoards_IsAmbiguous()
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
		var d = Decide(ctx, [State("s1", "defined", "spec", nodeId: Id("s1"))], links: Link("s1", "idea_spec", "faster-search"));
		d.Verdicts.Single().Message.Should().Be(
			"ambiguous links.idea_spec 'faster-search' (node 's1') — the slug matches nodes on boards: [ideas-a, ideas-b]; pass the target node's NodeId instead");
	}

	[Fact]
	public void Directed_TargetKindComesFromTheDeclaredDirection()
	{
		// The target end is DATA — a definition whose custom `doc` kind links a custom `rfc` kind via
		// a declared link kind resolves slugs against THAT kind's boards.
		var def = new MethodologyDefinition("custom",
		[
			new MethodologyKindDef("doc", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["doc"], [new WorkflowStatus("draft", "Draft", StatusKind.Open)], [])]),
			new MethodologyKindDef("rfc", QuickAddAllowed: true,
				[new MethodologyWorkflowDef(["rfc"], [new WorkflowStatus("open", "Open", StatusKind.Open)], [])]),
		])
		{
			LinkKinds = [new MethodologyLinkKindDef("doc_rfc", "doc realizes an rfc", LinkCategory.Process,
				new MethodologyLinkDirectionDef("doc", "rfc", "realizes"))],
		};
		var ctx = Ctx(runtime: MethodologyRuntime.From(def), kindSlug: "doc", board: "docs", specBoard: null,
			index: [Node("r1", "rfcs", "rfc", "streaming", "open", "rfc")],
			boards:
			[
				new EngineBoard("docs", "doc", Instance, Closed: false),
				new EngineBoard("rfcs", "rfc", Instance, Closed: false),
			]);
		var d = Decide(ctx, [State("d1", "draft", "doc", nodeId: Id("d1"))], links: Link("d1", "doc_rfc", "streaming"));
		d.Verdicts.Should().BeEmpty();
		d.Links.Single(l => l.Kind == "doc_rfc").TargetNodeId.Should().Be(Id("r1"));

		// ...and the miss message names that declared target kind, not `ideas`.
		var miss = Decide(ctx, [State("d1", "draft", "doc", nodeId: Id("d1"))], links: Link("d1", "doc_rfc", "ghost"));
		miss.Verdicts.Single().Message.Should().Be("links.doc_rfc 'ghost' (node 'd1') does not match any node on rfc board 'rfcs'");
	}
}
