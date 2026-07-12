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
}
