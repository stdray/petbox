using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// recurrence-and-session-provenance-as-board-fields: MethodologyRuntime.IsObservationKind is the
// ONE place BoardFieldConfig.Default / the fields dialog ask "is this an observation board" —
// centralizing what used to risk being a string comparison repeated at each call site (the SAME
// posture the card asked for, "по образцу уже существующего runtime.DeliveryOf(kindSlug)").
// Pure logic, no MCP/DB fixture needed — same style as BoardFieldConfigTests and
// MethodologyRuntimeViewDefaultsTests (both exercise MethodologyRuntime directly).
public sealed class MethodologyRuntimeObservationKindTests
{
	[Fact]
	public void PresetsOnly_ObservationSlug_IsObservationKind() =>
		MethodologyRuntime.PresetsOnly.IsObservationKind("observation").Should().BeTrue();

	[Fact]
	public void PresetsOnly_ObservationSlug_IsCaseInsensitive() =>
		MethodologyRuntime.PresetsOnly.IsObservationKind("OBSERVATION").Should().BeTrue();

	[Theory]
	[InlineData("work")]
	[InlineData("spec")]
	[InlineData("ideas")]
	[InlineData("intake")]
	[InlineData("totally-custom-kind")]
	[InlineData(null)]
	public void PresetsOnly_EveryOtherSlug_IsNotObservationKind(string? kindSlug) =>
		MethodologyRuntime.PresetsOnly.IsObservationKind(kindSlug).Should().BeFalse();

	// A DEFINITION-DECLARED kind resolves through the SAME whole-object ResolvedKind lookup
	// DeliveryOf/AutoWireFrom use (not the preset fallback) — a project that declares its OWN
	// `observation` kind (e.g. a custom methodology reusing the slug) still answers true, and a
	// declared kind under any OTHER slug answers false even though it shares the same workflow
	// shape as the preset observation kind.
	[Fact]
	public void DeclaredObservationKind_IsObservationKind()
	{
		var declared = new MethodologyKindDef("observation", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["observation"],
			[
				new WorkflowStatus("seen", "Seen", StatusKind.Open),
			], []),
		]);
		var runtime = MethodologyRuntime.From(new MethodologyDefinition("custom", [declared]));
		runtime.IsObservationKind("observation").Should().BeTrue();
	}

	[Fact]
	public void DeclaredNonObservationKind_IsNotObservationKind()
	{
		var declared = new MethodologyKindDef("finding", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["finding"],
			[
				new WorkflowStatus("seen", "Seen", StatusKind.Open),
			], []),
		]);
		var runtime = MethodologyRuntime.From(new MethodologyDefinition("custom", [declared]));
		runtime.IsObservationKind("finding").Should().BeFalse();
	}
}
