using PetBox.Tasks.Workflow;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// board-view-persistence resolution chain: explicit choice -> methodology defaultView ->
// builtin Tree default. `Resolve` is the single seam TaskBoardModel.LoadAsync calls
// (ResolvedViewMode = BoardViewModeRegistry.Resolve(ViewMode, Runtime.DefaultView(KindSlug)))
// — covered here as a pure function so the three-tier order is pinned down without a DB.
public sealed class BoardViewModeRegistryTests
{
	[Fact]
	public void ExplicitChoice_WinsOverMethodologyDefault() =>
		BoardViewModeRegistry.Resolve(requested: "kanban", methodologyDefault: "tree").Should().Be("kanban");

	[Fact]
	public void NoExplicitChoice_FallsBackToMethodologyDefault() =>
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: "kanban").Should().Be("kanban");

	[Fact]
	public void NeitherPresent_FallsBackToBuiltinTree() =>
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: null).Should().Be(BoardViewModeNames.Tree);

	[Fact]
	public void ExplicitChoice_UnrenderableName_FallsThroughToMethodologyDefault() =>
		// "bogus" isn't a registered mode at all — Find() returns null, so Resolve falls
		// through to the methodology default exactly as it would for any other unknown name.
		BoardViewModeRegistry.Resolve(requested: "bogus", methodologyDefault: "kanban").Should().Be("kanban");

	[Fact]
	public void BothUnrenderable_FallsBackToTree() =>
		BoardViewModeRegistry.Resolve(requested: "bogus", methodologyDefault: "also-bogus").Should().Be(BoardViewModeNames.Tree);

	[Fact]
	public void ResolutionIsCaseInsensitive() =>
		BoardViewModeRegistry.Resolve(requested: "KANBAN", methodologyDefault: null).Should().Be("kanban");

	[Theory]
	[InlineData(BoardViewModeNames.Tree)]
	[InlineData(BoardViewModeNames.Tags)]
	[InlineData(BoardViewModeNames.Kanban)]
	[InlineData(BoardViewModeNames.Outline)]
	[InlineData(BoardViewModeNames.Table)]
	public void AllKnownModes_AreRenderable(string mode)
	{
		// board-view-mode-framework's follow-up shipped kanban/outline/table's partials — every
		// BoardViewModeNames constant is now both a legal NAME (IsKnown) and RENDERABLE
		// (a registry entry with a real partial); no name is reserved-but-unshipped anymore.
		BoardViewModeNames.IsKnown(mode).Should().BeTrue();
		BoardViewModeRegistry.IsRenderable(mode).Should().BeTrue();
	}

	// board-tag-grouping-disabled: Tags is RENDERABLE (a real registry entry, a real partial —
	// IsRenderable/Find still see it exactly like any other entry, and the switcher still draws
	// its button, just disabled — see TaskBoard.cshtml) but NOT SELECTABLE — Resolve must never
	// land a request on it, explicit `?view=tags` or an inherited methodology default alike.
	[Fact]
	public void TagsEntry_IsRenderableButNotSelectable()
	{
		var tags = BoardViewModeRegistry.Find(BoardViewModeNames.Tags);
		tags.Should().NotBeNull();
		tags!.DisabledReason.Should().NotBeNull();

		BoardViewModeRegistry.IsRenderable(BoardViewModeNames.Tags).Should().BeTrue();
		BoardViewModeRegistry.IsSelectable(BoardViewModeNames.Tags).Should().BeFalse();
		// An explicit `?view=tags` request falls through to Tree (no other tier given) — never
		// renders the grouping pane.
		BoardViewModeRegistry.Resolve(requested: "tags", methodologyDefault: null).Should().Be(BoardViewModeNames.Tree);
		// A methodology defaultView of "tags" must equally never leak through — DisabledReason
		// applies at every resolution tier, not just the explicit one.
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: "tags").Should().Be(BoardViewModeNames.Tree);
		// Even when the OTHER tier is also "tags", nothing resolves to it — falls all the way to Tree.
		BoardViewModeRegistry.Resolve(requested: "tags", methodologyDefault: "tags").Should().Be(BoardViewModeNames.Tree);
	}

	[Fact]
	public void ExplicitChoice_Disabled_FallsThroughToASelectableMethodologyDefault() =>
		// The explicit tier is disabled but the methodology-default tier names a real, selectable
		// mode — Resolve must fall through to it rather than stopping at Tree prematurely.
		BoardViewModeRegistry.Resolve(requested: "tags", methodologyDefault: "kanban").Should().Be("kanban");

	[Fact]
	public void OnlyTagsIsDisabled_TheOtherFourModesStaySelectable()
	{
		// Pins today's contract so a future addition doesn't silently disable itself by copy-paste
		// (e.g. cloning the Tags entry as a starting point for a new one and forgetting to clear
		// DisabledReason).
		BoardViewModeRegistry.Entries.Where(e => e.DisabledReason is not null).Select(e => e.Key)
			.Should().Equal(BoardViewModeNames.Tags);
		BoardViewModeRegistry.Entries.Where(e => e.DisabledReason is null).Select(e => e.Key)
			.Should().Equal(BoardViewModeNames.Tree, BoardViewModeNames.Kanban, BoardViewModeNames.Outline, BoardViewModeNames.Table);
	}
}
