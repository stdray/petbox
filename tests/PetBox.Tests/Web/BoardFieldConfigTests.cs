using PetBox.Tasks.Workflow;
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// board-view-fields: the property-set config every view partial reads — covered here as pure
// logic (no HTTP round-trip) so the parse/round-trip/default rules are pinned down independent of
// TaskBoardModel's own integration coverage (ModuleViewsTests).
public sealed class BoardFieldConfigTests
{
	[Fact]
	public void FromKeys_UnknownKeysAreSilentlyDropped()
	{
		// Never a 500 on an old saved value / stale link once a future BoardFieldNames entry is
		// removed or renamed — same tolerance BoardViewModeRegistry already gives unknown view names.
		var cfg = BoardFieldConfig.FromKeys([BoardFieldNames.Type, "bogus", BoardFieldNames.Tags]);
		cfg.Type.Should().BeTrue();
		cfg.Tags.Should().BeTrue();
		cfg.Status.Should().BeFalse();
		cfg.Priority.Should().BeFalse();
	}

	[Fact]
	public void FromKeys_NullOrEmpty_YieldsNone()
	{
		BoardFieldConfig.FromKeys(null).Should().Be(BoardFieldConfig.None);
		BoardFieldConfig.FromKeys([]).Should().Be(BoardFieldConfig.None);
	}

	[Fact]
	public void FromKeys_IsCaseInsensitive() =>
		BoardFieldConfig.FromKeys(["STATUS", "Tags"]).Should().Be(new BoardFieldConfig(
			Type: false, Status: true, Priority: false, Tags: true, UpdatedAt: false,
			Delivery: false, BlockedBy: false, Body: false));

	[Fact]
	public void ToCsv_RoundTripsThroughFromKeys()
	{
		var cfg = new BoardFieldConfig(Type: true, Status: false, Priority: true, Tags: false, UpdatedAt: true, Delivery: false, BlockedBy: true, Body: false);
		var roundTripped = BoardFieldConfig.FromKeys(cfg.ToCsv().Split(',', StringSplitOptions.RemoveEmptyEntries));
		roundTripped.Should().Be(cfg);
	}

	[Fact]
	public void ToCsv_EmptyConfig_IsEmptyString() =>
		BoardFieldConfig.None.ToCsv().Should().BeEmpty();

	[Fact]
	public void Has_ReadsTheMatchingProperty()
	{
		var cfg = new BoardFieldConfig(Type: true, Status: false, Priority: true, Tags: false, UpdatedAt: false, Delivery: false, BlockedBy: false, Body: false);
		cfg.Has(BoardFieldNames.Type).Should().BeTrue();
		cfg.Has(BoardFieldNames.Status).Should().BeFalse();
		cfg.Has("bogus").Should().BeFalse();
	}

	// board-view-fields bullet 2: delivery only computes on a kind that actually rolls it up
	// (MethodologyRuntime.DeliveryOf) — work/intake nodes always carry Delivery:null, so the
	// column/badge would be permanently empty there. The quartet preset's `spec` kind computes
	// delivery; `work` does not.
	[Fact]
	public void Default_DeliveryField_OnlyOnForAKindThatComputesIt()
	{
		var runtime = MethodologyRuntime.PresetsOnly;

		BoardFieldConfig.Default(BoardViewModeNames.Table, runtime, "spec", outlineBodyDefault: false)
			.Delivery.Should().BeTrue();
		BoardFieldConfig.Default(BoardViewModeNames.Table, runtime, "work", outlineBodyDefault: false)
			.Delivery.Should().BeFalse();
	}

	// board-view-fields bullet 3: Status defaults OFF in tree/outline ("cuts the eye") and in
	// kanban (redundant with the column), but ON in table (its whole point is the column list).
	[Theory]
	[InlineData(BoardViewModeNames.Tree, false)]
	[InlineData(BoardViewModeNames.Outline, false)]
	[InlineData(BoardViewModeNames.Kanban, false)]
	[InlineData(BoardViewModeNames.Table, true)]
	public void Default_StatusField_PerMode(string viewMode, bool expected) =>
		BoardFieldConfig.Default(viewMode, MethodologyRuntime.PresetsOnly, "work", outlineBodyDefault: false)
			.Status.Should().Be(expected);

	// board-view-defaults-not-applied-existing-instances' sibling guard: Default is PURE CODE, not
	// methodology-definition data — an unrecognized/未 declared kind slug (as a pre-field-existing
	// definition would look, or a brand-new custom kind) still resolves a sane default instead of
	// throwing or reading stale null-as-"no opinion" the way a per-kind MethodologyKindDef field
	// would have to guard against.
	[Fact]
	public void Default_UnknownKindSlug_StillResolvesWithoutThrowing()
	{
		var act = () => BoardFieldConfig.Default(BoardViewModeNames.Kanban, MethodologyRuntime.PresetsOnly, "totally-custom-kind", outlineBodyDefault: false);
		act.Should().NotThrow();
	}

	// Outline's Body field seeds off the kind's OWN inline-lazy opt-in, not a fixed default —
	// preserves exactly what the pre-config outline partial showed (spec bodies lazy-reveal by
	// default, every other kind doesn't).
	[Fact]
	public void Default_Outline_BodyField_FollowsTheCallerSuppliedFlag()
	{
		BoardFieldConfig.Default(BoardViewModeNames.Outline, MethodologyRuntime.PresetsOnly, "spec", outlineBodyDefault: true)
			.Body.Should().BeTrue();
		BoardFieldConfig.Default(BoardViewModeNames.Outline, MethodologyRuntime.PresetsOnly, "spec", outlineBodyDefault: false)
			.Body.Should().BeFalse();
	}
}
