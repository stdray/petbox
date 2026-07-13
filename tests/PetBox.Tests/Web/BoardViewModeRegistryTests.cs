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
		BoardViewModeRegistry.Resolve(requested: "tags", methodologyDefault: "tree").Should().Be("tags");

	[Fact]
	public void NoExplicitChoice_FallsBackToMethodologyDefault() =>
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: "tags").Should().Be("tags");

	[Fact]
	public void NeitherPresent_FallsBackToBuiltinTree() =>
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: null).Should().Be(BoardViewModeNames.Tree);

	[Fact]
	public void ExplicitChoice_UnrenderableName_FallsThroughToMethodologyDefault() =>
		// "bogus" isn't a registered mode at all — Find() returns null, so Resolve falls
		// through to the methodology default exactly as it would for any other unknown name.
		BoardViewModeRegistry.Resolve(requested: "bogus", methodologyDefault: "tags").Should().Be("tags");

	[Fact]
	public void BothUnrenderable_FallsBackToTree() =>
		BoardViewModeRegistry.Resolve(requested: "bogus", methodologyDefault: "also-bogus").Should().Be(BoardViewModeNames.Tree);

	[Fact]
	public void ResolutionIsCaseInsensitive() =>
		BoardViewModeRegistry.Resolve(requested: "TAGS", methodologyDefault: null).Should().Be("tags");

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

	// board-tag-grouping-hidden: Tags is pulled from the switcher (TaskBoard.cshtml filters
	// `Entries.Where(e => !e.Hidden)`) but MUST stay exactly as renderable/resolvable as any
	// other entry — Hidden is a switcher-display concern only, never a Find/Resolve concern.
	[Fact]
	public void TagsEntry_IsHidden_ButFullyRenderableAndResolvable()
	{
		var tags = BoardViewModeRegistry.Find(BoardViewModeNames.Tags);
		tags.Should().NotBeNull();
		tags!.Hidden.Should().BeTrue();

		BoardViewModeRegistry.IsRenderable(BoardViewModeNames.Tags).Should().BeTrue();
		BoardViewModeRegistry.Resolve(requested: "tags", methodologyDefault: null).Should().Be("tags");
		// A methodology defaultView of "tags" (a hidden-but-real mode) must still resolve to it —
		// Hidden must never leak into the resolution chain, only the switcher's own render loop.
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: "tags").Should().Be("tags");
	}

	[Fact]
	public void OnlyTagsIsHidden_TheOtherFourModesStayInTheSwitcher()
	{
		// Pins today's contract so a future addition doesn't silently hide itself by copy-paste
		// (e.g. cloning the Tags entry as a starting point for a new one and forgetting to flip
		// Hidden back to false).
		BoardViewModeRegistry.Entries.Where(e => e.Hidden).Select(e => e.Key)
			.Should().Equal(BoardViewModeNames.Tags);
	}
}
