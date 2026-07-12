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
		// "bogus" isn't a registered mode at all — same silent degradation an unrenderable-
		// but-known name (e.g. "kanban" before its partial ships) gets, since Find() returns
		// null for both today.
		BoardViewModeRegistry.Resolve(requested: "bogus", methodologyDefault: "tags").Should().Be("tags");

	[Fact]
	public void ReservedButUnshippedMode_DegradesToTree_NeverThrows() =>
		// kanban/outline/table are KNOWN names (BoardViewModeNames.All, so a methodology
		// definition may legally set them — see BoardViewModeNamesTests) but have no registry
		// entry yet — Resolve degrades silently, exactly like an unknown URL value would.
		BoardViewModeRegistry.Resolve(requested: null, methodologyDefault: BoardViewModeNames.Kanban)
			.Should().Be(BoardViewModeNames.Tree);

	[Fact]
	public void BothUnrenderable_FallsBackToTree() =>
		BoardViewModeRegistry.Resolve(requested: "bogus", methodologyDefault: "also-bogus").Should().Be(BoardViewModeNames.Tree);

	[Fact]
	public void ResolutionIsCaseInsensitive() =>
		BoardViewModeRegistry.Resolve(requested: "TAGS", methodologyDefault: null).Should().Be("tags");

	[Fact]
	public void Entries_TreeAndTags_AreRenderableToday()
	{
		BoardViewModeRegistry.IsRenderable(BoardViewModeNames.Tree).Should().BeTrue();
		BoardViewModeRegistry.IsRenderable(BoardViewModeNames.Tags).Should().BeTrue();
	}

	[Theory]
	[InlineData(BoardViewModeNames.Kanban)]
	[InlineData(BoardViewModeNames.Outline)]
	[InlineData(BoardViewModeNames.Table)]
	public void ReservedModes_AreKnownButNotYetRenderable(string mode)
	{
		// Pins the exact "not yet" boundary board-view-mode-framework hands to the next
		// worker: legal to NAME (BoardViewModeNames.IsKnown), not yet legal to RENDER
		// (BoardViewModeRegistry.IsRenderable) — adding one entry to Entries flips this.
		BoardViewModeNames.IsKnown(mode).Should().BeTrue();
		BoardViewModeRegistry.IsRenderable(mode).Should().BeFalse();
	}
}
