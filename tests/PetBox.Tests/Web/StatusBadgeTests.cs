using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.Shared;

namespace PetBox.Tests.Web;

// Regression coverage for StatusBadgeModel.Show's spec-board noise suppression
// (spec-board-status-noise #9) across BOTH shapes a `spec` board can have
// (presetkind-spec-blind-spot). The OLD guard, `Runtime.PresetKind(KindSlug) !=
// BoardKind.Spec`, nulls out for ANY definition-resolved kind — and $system's real
// `spec` board is exactly that shape: the quartet preset renders its kinds, including
// `spec`, VERBATIM into the instance's stored definition (RenderPresetDefinition), so
// `IsDefinedKind("spec")` is TRUE in production and `PresetKind("spec")` reads null.
// On that shape the old guard evaluated `null != BoardKind.Spec` == true, so `Show`
// was ALWAYS true regardless of terminality — the noise suppression silently never
// fired on a real project's spec board. This is TasksService.cs's sibling site named
// in the presetkind-spec-blind-spot bug (StatusBadge.Show); fixed to
// Runtime.IsSpecKind(KindSlug), which compares the EFFECTIVE kind name and reads
// correctly for both a bare preset kind slug and a definition-declared one.
public sealed class StatusBadgeTests
{
	// Mirrors MethodologyPresets' own SpecKind shape (kind slug "spec": defined ->
	// deprecated, and the feature/bug delivery roll-up) — exactly what RenderPresetDefinition
	// materializes for a real quartet-provisioned project, just declared directly here instead
	// of via the (internal) preset renderer. The Delivery config is part of that shape and is
	// what now IDENTIFIES a spec board to the presentation layer (StatusBadgeModel.Show reads
	// DeliveryOf(...) is not null, not a "spec" name literal): a real spec board always carries
	// it, so the noise suppression fires exactly as it does in production.
	static readonly MethodologyDefinition SpecDefinitionResolved = new("spec-def-resolved",
	[
		new MethodologyKindDef("spec", QuickAddAllowed: false,
		[
			new MethodologyWorkflowDef(["spec"],
				[
					new("defined", "Defined", StatusKind.Open),
					new("deprecated", "Deprecated", StatusKind.TerminalCancel),
				],
				[new("defined", "deprecated")]),
		])
		{
			Delivery = new MethodologyDeliveryDef(["feature"], ["bug"], "task_spec"),
		},
	]);

	[Fact]
	public void Show_BarePresetWiredBoard_HidesDefined_ShowsDeprecated()
	{
		var badgeDefined = new StatusBadgeModel(MethodologyRuntime.PresetsOnly, "spec", "defined");
		var badgeDeprecated = new StatusBadgeModel(MethodologyRuntime.PresetsOnly, "spec", "deprecated");

		badgeDefined.Show.Should().BeFalse("a bare-preset spec board hides the near-universal `defined` status");
		badgeDeprecated.Show.Should().BeTrue("a terminal status always shows, even on a spec board");
	}

	// THE regression: same assertions, but on a DEFINITION-RESOLVED runtime — the shape
	// $system's real spec board actually has. This is the shape the test suite never
	// built before (spec board fixtures were always the bare preset string), which is
	// exactly why the old PresetKind-based guard's blind spot went unnoticed.
	[Fact]
	public void Show_DefinitionResolvedWiredBoard_HidesDefined_ShowsDeprecated()
	{
		var runtime = new MethodologyRuntime(SpecDefinitionResolved);
		var badgeDefined = new StatusBadgeModel(runtime, "spec", "defined");
		var badgeDeprecated = new StatusBadgeModel(runtime, "spec", "deprecated");

		badgeDefined.Show.Should().BeFalse(
			"a DEFINITION-RESOLVED spec board (the shape $system actually has) must still hide the " +
			"non-terminal `defined` status — PresetKind nulling out for a defined kind must not silently " +
			"disable the noise suppression (presetkind-spec-blind-spot)");
		badgeDeprecated.Show.Should().BeTrue("a terminal status always shows, even on a definition-resolved spec board");
	}

	// --- status pill colour comes from StatusKind METADATA (work `node-render-design-layer`) -----

	// A definition built so that every status NAME lies about its kind: a status whose slug and
	// display name are literally "Done" is classified Open, and one called "Broken" is a SUCCESS
	// terminal. Nothing but StatusKind can produce the right pill on this board — a rule that read
	// the word, from a body or from the label, gets every single one of these backwards.
	static readonly MethodologyDefinition MisleadingNames = new("misleading",
	[
		new MethodologyKindDef("task", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["task"],
				[
					new("Done", "Done", StatusKind.Open),
					new("Broken", "Broken", StatusKind.TerminalOk),
					new("Live", "Live", StatusKind.TerminalCancel),
				],
				[new("Done", "Broken"), new("Done", "Live")]),
		]),
	]);

	[Theory]
	[InlineData("Done", "status-pill status-pill-proposed")]
	[InlineData("Broken", "status-pill status-pill-live")]
	[InlineData("Live", "status-pill status-pill-broken")]
	public void CssClass_FollowsStatusKind_NotTheWord(string status, string expected)
	{
		var runtime = new MethodologyRuntime(MisleadingNames);
		new StatusBadgeModel(runtime, "task", status).CssClass.Should().Be(expected,
			"the pill is keyed on MethodologyRuntime.StatusKindOf — the status text, and anything " +
			"a node's BODY says, is not an input to it at all");
	}

	[Fact]
	public void CssClass_UnknownStatus_GetsNeutralPair_NotASemanticOne()
	{
		// An unclassifiable status must not claim a meaning it does not have.
		var runtime = new MethodologyRuntime(MisleadingNames);
		new StatusBadgeModel(runtime, "task", "no-such-status").CssClass
			.Should().Be("status-pill status-pill-muted");
	}
}
