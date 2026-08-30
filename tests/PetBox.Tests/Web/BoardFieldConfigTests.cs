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
		cfg.Slug.Should().BeFalse();
	}

	// board-fields-slug-missing: slug (the node key) is a selectable field like any other — this
	// pins down FromKeys actually recognizing it (BoardFieldNames.IsKnown), not just Default().
	[Fact]
	public void FromKeys_RecognizesSlug()
	{
		var cfg = BoardFieldConfig.FromKeys([BoardFieldNames.Slug]);
		cfg.Slug.Should().BeTrue();
		cfg.Type.Should().BeFalse();
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
			Slug: false, Type: false, Status: true, Priority: false, Tags: true, UpdatedAt: false,
			Delivery: false, BlockedBy: false, Body: false));

	[Fact]
	public void ToCsv_RoundTripsThroughFromKeys()
	{
		var cfg = new BoardFieldConfig(Slug: true, Type: true, Status: false, Priority: true, Tags: false, UpdatedAt: true, Delivery: false, BlockedBy: true, Body: false);
		var roundTripped = BoardFieldConfig.FromKeys(cfg.ToCsv().Split(',', StringSplitOptions.RemoveEmptyEntries));
		roundTripped.Should().Be(cfg);
	}

	[Fact]
	public void ToCsv_EmptyConfig_IsEmptyString() =>
		BoardFieldConfig.None.ToCsv().Should().BeEmpty();

	[Fact]
	public void Has_ReadsTheMatchingProperty()
	{
		var cfg = new BoardFieldConfig(Slug: true, Type: true, Status: false, Priority: true, Tags: false, UpdatedAt: false, Delivery: false, BlockedBy: false, Body: false);
		cfg.Has(BoardFieldNames.Slug).Should().BeTrue();
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

	// board-fields-slug-missing: Slug defaults ON in every mode — kanban never showed the node key
	// at all before this field existed (the reported bug) and the owner asked for it ON there
	// explicitly; tree/tags, outline and table already rendered it UNCONDITIONALLY before this
	// config existed, so ON here is "nothing visibly disappears" for them, not a new opinion.
	[Theory]
	[InlineData(BoardViewModeNames.Tree)]
	[InlineData(BoardViewModeNames.Outline)]
	[InlineData(BoardViewModeNames.Kanban)]
	[InlineData(BoardViewModeNames.Table)]
	public void Default_SlugField_OnInEveryMode(string viewMode) =>
		BoardFieldConfig.Default(viewMode, MethodologyRuntime.PresetsOnly, "work", outlineBodyDefault: false)
			.Slug.Should().BeTrue();

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

	// recurrence-and-session-provenance-as-board-fields: FromKeys/Has round-trip the two new keys
	// exactly like the original nine (BoardFieldNames.Recurrence/Sessions).
	[Fact]
	public void FromKeys_RecognizesRecurrenceAndSessions()
	{
		var cfg = BoardFieldConfig.FromKeys([BoardFieldNames.Recurrence, BoardFieldNames.Sessions]);
		cfg.Recurrence.Should().BeTrue();
		cfg.Sessions.Should().BeTrue();
		cfg.Has(BoardFieldNames.Recurrence).Should().BeTrue();
		cfg.Has(BoardFieldNames.Sessions).Should().BeTrue();
		cfg.Slug.Should().BeFalse();
	}

	[Fact]
	public void ToCsv_RoundTripsThroughFromKeys_WithRecurrenceAndSessions()
	{
		var cfg = new BoardFieldConfig(
			Slug: true, Type: false, Status: false, Priority: false, Tags: false, UpdatedAt: false,
			Delivery: false, BlockedBy: false, Body: false, Recurrence: true, Sessions: true);
		var roundTripped = BoardFieldConfig.FromKeys(cfg.ToCsv().Split(',', StringSplitOptions.RemoveEmptyEntries));
		roundTripped.Should().Be(cfg);
	}

	// spec observation-recurrence-visible-on-card: Recurrence defaults ON, but ONLY for the
	// observation kind — the whole reason BoardFieldConfig.Default now takes a runtime-resolved
	// "is this an observation board" bit (MethodologyRuntime.IsObservationKind) instead of a
	// hardcoded false. Every view mode gets the SAME answer — the mode switch never touches it.
	[Theory]
	[InlineData(BoardViewModeNames.Tree)]
	[InlineData(BoardViewModeNames.Outline)]
	[InlineData(BoardViewModeNames.Kanban)]
	[InlineData(BoardViewModeNames.Table)]
	public void Default_RecurrenceField_OnForObservationKind_InEveryMode(string viewMode) =>
		BoardFieldConfig.Default(viewMode, MethodologyRuntime.PresetsOnly, "observation", outlineBodyDefault: false)
			.Recurrence.Should().BeTrue();

	[Theory]
	[InlineData(BoardViewModeNames.Tree)]
	[InlineData(BoardViewModeNames.Outline)]
	[InlineData(BoardViewModeNames.Kanban)]
	[InlineData(BoardViewModeNames.Table)]
	public void Default_RecurrenceField_OffForNonObservationKinds_InEveryMode(string viewMode) =>
		BoardFieldConfig.Default(viewMode, MethodologyRuntime.PresetsOnly, "work", outlineBodyDefault: false)
			.Recurrence.Should().BeFalse();

	// board-view-fields' own "don't default to noise nobody asked for" posture, applied to the new
	// Sessions field: opt-in everywhere, unlike Recurrence's kind-gated default — including on an
	// observation board, where Recurrence itself just defaulted ON above (the two fields are
	// independent toggles, not a package deal).
	[Theory]
	[InlineData(BoardViewModeNames.Tree, "observation")]
	[InlineData(BoardViewModeNames.Outline, "observation")]
	[InlineData(BoardViewModeNames.Kanban, "observation")]
	[InlineData(BoardViewModeNames.Table, "work")]
	public void Default_SessionsField_AlwaysOff(string viewMode, string kindSlug) =>
		BoardFieldConfig.Default(viewMode, MethodologyRuntime.PresetsOnly, kindSlug, outlineBodyDefault: false)
			.Sessions.Should().BeFalse();

	// board-view-fields bullet 3 (the highest-risk part of recurrence-and-session-provenance-as-
	// board-fields): a saved preference from BEFORE FieldsKnown existed (FieldsKnown null) must
	// give the NEW keys their fresh default — that is what makes the reported bug ("×1 counter
	// missing from a card whose board-level Fields CSV predates the recurrence key") go away on
	// the FIRST post-deploy page load rather than only after the owner re-applies the dialog —
	// while STILL keeping the choices the owner actually made. Null FieldsKnown means "written by
	// a build that knew the nine LegacyKnownCsv keys", not "the owner knew nothing": resetting
	// their whole layout to default would fix the counter by breaking everything around it.
	[Fact]
	public void FromSaved_FieldsKnownNull_DefaultsNewKeys_ButKeepsLegacyChoices()
	{
		var defaults = new BoardFieldConfig(
			Slug: true, Type: false, Status: false, Priority: false, Tags: true, UpdatedAt: false,
			Delivery: false, BlockedBy: true, Body: true, Recurrence: true, Sessions: false);
		// A pre-existing saved CSV that (deliberately, from the owner's POV) turned Tags and
		// BlockedBy off and kept Slug/Body on — but never recorded FieldsKnown.
		var result = BoardFieldConfig.FromSaved(fieldsCsv: "slug,body", fieldsKnownCsv: null, defaults);

		result.Slug.Should().BeTrue();
		result.Body.Should().BeTrue();
		// Legacy keys the owner left unchecked stay unchecked, even though `defaults` turns them on.
		result.Tags.Should().BeFalse();
		result.BlockedBy.Should().BeFalse();
		// Keys that did not exist at save time take the default — the actual bug fix.
		result.Recurrence.Should().BeTrue();
		result.Sessions.Should().BeFalse();
	}

	// The other half of bullet 3: once FieldsKnown IS recorded, a key INSIDE it is the owner's
	// explicit choice — including a DELIBERATE off — and must NOT fall back to default. This is
	// the naive-implementation trap the card calls out: "key absent -> use default" would make it
	// impossible to ever turn an existing field off.
	[Fact]
	public void FromSaved_KnownKey_HonoursExplicitOff_EvenWhenDefaultWouldTurnItOn()
	{
		var defaults = new BoardFieldConfig(
			Slug: true, Type: false, Status: false, Priority: false, Tags: true, UpdatedAt: false,
			Delivery: false, BlockedBy: true, Body: true, Recurrence: true, Sessions: false);
		// Tags/Body/Recurrence are all ON in `defaults` above; the saved Fields csv leaves all
		// three out while FieldsKnown says the owner DID see all of them (the full vocabulary).
		var result = BoardFieldConfig.FromSaved(
			fieldsCsv: "slug", fieldsKnownCsv: BoardFieldNames.AllCsv, defaults);
		result.Slug.Should().BeTrue();
		result.Tags.Should().BeFalse();
		result.Body.Should().BeFalse();
		result.Recurrence.Should().BeFalse();
	}

	// A key that's simply NEW relative to what FieldsKnown recorded (a real future-field scenario,
	// not the "nothing was ever recorded" edge case above) still falls back to default, while a
	// key already inside FieldsKnown keeps the owner's explicit choice — the actual forward-compat
	// promise: "any future field reaches an already-customized board without resurrecting fields
	// the owner deliberately turned off."
	[Fact]
	public void FromSaved_KeyMissingFromFieldsKnown_FallsBackToDefault_ButKnownKeysStayExplicit()
	{
		var defaults = new BoardFieldConfig(
			Slug: true, Type: false, Status: false, Priority: false, Tags: true, UpdatedAt: false,
			Delivery: false, BlockedBy: false, Body: false, Recurrence: true, Sessions: false);
		// FieldsKnown recorded every key EXCEPT recurrence (simulating a preference saved between
		// this task's BoardFieldNames.Recurrence landing in code and the owner's next dialog Apply
		// — an intermediate state a rolling deploy can genuinely produce). The owner explicitly
		// turned Tags off (known, absent from Fields) and left Slug on (known, present).
		var knownWithoutRecurrence = string.Join(",",
			BoardFieldNames.Options.Select(o => o.Key).Where(k => k != BoardFieldNames.Recurrence));
		var result = BoardFieldConfig.FromSaved(fieldsCsv: "slug", fieldsKnownCsv: knownWithoutRecurrence, defaults);
		result.Slug.Should().BeTrue();
		result.Tags.Should().BeFalse(); // known + explicitly unchecked -> stays off, not defaults' "on"
		result.Recurrence.Should().BeTrue(); // unknown -> falls back to defaults' "on"
	}

	// An empty-string FieldsKnown is the same case as null (a preference written before the
	// mechanism existed), not "a set with nothing in it" — same LegacyKnownCsv substitution.
	[Fact]
	public void FromSaved_FieldsKnownEmptyString_IsTreatedLikeNull()
	{
		var defaults = BoardFieldConfig.None with { Tags = true, Recurrence = true };
		var result = BoardFieldConfig.FromSaved(fieldsCsv: "slug", fieldsKnownCsv: "", defaults);

		result.Slug.Should().BeTrue();
		result.Tags.Should().BeFalse();   // legacy key, explicitly unchecked -> stays off
		result.Recurrence.Should().BeTrue(); // not a legacy key -> takes the default
	}
}
