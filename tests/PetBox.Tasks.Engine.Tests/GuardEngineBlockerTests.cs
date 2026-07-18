using static PetBox.Tasks.Engine.Tests.EngineFixture;

namespace PetBox.Tasks.Engine.Tests;

// The blocker invariant — "a Blocked task must name a blocker" — judged against PREFETCHED edge
// data (condition 2) instead of the old per-node live relation read.
public sealed class GuardEngineBlockerTests
{
	[Fact]
	public void BlockedTask_WithoutAnyBlocker_IsRefused()
	{
		var v = GuardEngine.RequireBlockers(Ctx(), [State("t1", "Blocked", "feature", nodeId: Id("t1"))], NoResolvedLinks);
		v.Should().Be(new MethodologyVerdict("t1",
			"a Blocked task must name a blocker — provide blockedBy (node 't1')",
			VerdictKind.InvalidArgument));
	}

	[Fact]
	public void BlockedTask_NamingABlockerInThisCall_Passes() =>
		GuardEngine.RequireBlockers(Ctx(), [State("t1", "Blocked", "feature", nodeId: Id("t1"))], Blocks(("t1", Id("b1"))))
			.Should().BeNull();

	[Fact]
	public void BlockedTask_WithAnAlreadyActiveInboundBlocksEdge_Passes()
	{
		// Moving an already-blocked node without re-stating blockedBy is legal: the edge is live.
		var ctx = Ctx(blockerEdges: Edges((Id("t1"), [Id("b1")])));
		GuardEngine.RequireBlockers(ctx, [State("t1", "Blocked", "feature", nodeId: Id("t1"))], NoResolvedLinks).Should().BeNull();
	}

	[Fact]
	public void BlockedTask_WithAnEmptyEdgeList_IsRefused()
	{
		// Present-but-empty must read the same as absent (the old live read came back empty).
		var ctx = Ctx(blockerEdges: Edges((Id("t1"), [])));
		GuardEngine.RequireBlockers(ctx, [State("t1", "Blocked", "feature", nodeId: Id("t1"))], NoResolvedLinks)!.Node.Should().Be("t1");
	}

	[Fact]
	public void NodeBornBlockedInThisCall_HasNoInboundEdgeYet_AndIsRefused()
	{
		// Its NodeId is fresh, so it cannot appear in the prefetched map — exactly as the old
		// per-node live read came back empty for it.
		var ctx = Ctx(blockerEdges: Edges((Id("someone-else"), [Id("b1")])));
		GuardEngine.RequireBlockers(ctx, [State("t1", "Blocked", "feature", nodeId: Id("t1"))], NoResolvedLinks)!.Node.Should().Be("t1");
	}

	[Fact]
	public void NonBlockedStatuses_AreNotJudged()
	{
		foreach (var s in new[] { "Pending", "InProgress", "Review", "Done", "Cancelled" })
			GuardEngine.RequireBlockers(Ctx(), [State("t1", s, "feature", nodeId: Id("t1"))], NoResolvedLinks).Should().BeNull();
	}

	[Fact]
	public void BlockedMatching_IsCaseInsensitive() =>
		GuardEngine.RequireBlockers(Ctx(), [State("t1", "blocked", "feature", nodeId: Id("t1"))], NoResolvedLinks)!.Node.Should().Be("t1");

	[Fact]
	public void NonWorkKinds_AreNotJudged()
	{
		// The invariant is work's alone; a `Blocked`-named status on another kind is not it.
		var ctx = Ctx(kindSlug: "ideas", board: IdeasBoardName);
		GuardEngine.RequireBlockers(ctx, [State("i1", "Blocked", "idea", nodeId: Id("i1"))], NoResolvedLinks).Should().BeNull();
	}

	[Fact]
	public void WorkKind_IsDetectedOnADEFINEDKindToo_NotOnlyOnTheBarePreset()
	{
		// presetkind-spec-blind-spot: a real quartet-provisioned project stores `work` as a DEFINED
		// kind slug (RenderPresetDefinition materializes the preset verbatim), which is why
		// PresetKind(...) == BoardKind.Work read null and this invariant once shipped never firing
		// on any real board. Both runtime shapes must indict.
		foreach (var runtime in new[] { Quartet, Presets })
			GuardEngine.RequireBlockers(Ctx(runtime: runtime), [State("t1", "Blocked", "feature", nodeId: Id("t1"))], NoResolvedLinks)
				.Should().NotBeNull($"the blocker invariant must fire on a {(runtime == Quartet ? "defined" : "preset")} work kind");
	}

	[Fact]
	public void FirstOffendingNode_InBatchOrder_IsTheOneIndicted()
	{
		var v = GuardEngine.RequireBlockers(Ctx(),
			[State("ok", "InProgress", "feature", nodeId: Id("ok")), State("bad1", "Blocked", "feature", nodeId: Id("bad1")), State("bad2", "Blocked", "feature", nodeId: Id("bad2"))],
			NoResolvedLinks);
		v!.Node.Should().Be("bad1");
	}
}
