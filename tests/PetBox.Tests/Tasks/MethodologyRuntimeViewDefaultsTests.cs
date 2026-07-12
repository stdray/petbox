using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// board-view-defaults-not-applied-existing-instances: a board provisioned from the
// quartet/classic BUILTIN TEMPLATE materializes each preset MethodologyKindDef VERBATIM into
// the instance's stored MethodologyDefinition at creation time (RenderPresetDefinition). An
// instance created BEFORE MethodologyKindDef.DefaultView/OutlineReveal existed therefore
// stores a kind-def that declares everything else (workflows, statuses, transitions,
// quickAddAllowed, link constraints) but has DefaultView/OutlineReveal = null — exactly what
// happened to the four `$system` boards in production: all four opened in `tree` instead of
// their methodology defaults (work→kanban, spec→outline+inline-lazy, intake→table).
//
// The existing MethodologyRuntimeTests/MethodologyRuntimeUiTests fixtures don't catch this
// because they build FRESH definitions (via RiskDefinition() or MethodologyPresets directly),
// which already carry the new fields — the exact miss this suite exists to close. These
// tests hand-build a kind-def that OMITS DefaultView/OutlineReveal (simulating the pre-field
// stored JSON, where the fields are simply absent and deserialize to null) while declaring
// everything else a real materialized `work`/`spec` kind would.
public sealed class MethodologyRuntimeViewDefaultsTests
{
	// A `work` kind-def shaped like a pre-field materialized copy: workflows/statuses/
	// transitions/link constraints present (the kind is fully declared), DefaultView/
	// OutlineReveal simply never set (omitted from the record initializer, same as an old
	// JSON document with no `defaultView` property would deserialize).
	static readonly MethodologyKindDef StaleWorkKind = new("work", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["feature", "bug", "chore"],
			[
				new WorkflowStatus("Pending", "Pending", StatusKind.Open),
				new WorkflowStatus("InProgress", "In progress", StatusKind.Open),
				new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
			],
			[
				new MethodologyTransitionDef("Pending", "InProgress"),
				new MethodologyTransitionDef("InProgress", "Done", RequiresApproval: true),
			]),
	])
	{
		LinkConstraints =
		[
			new MethodologyLinkConstraintDef("feature", "task_spec") { TargetKind = "spec" },
		],
	};

	// A `spec` kind-def shaped the same way: fully declared, view-mode fields omitted.
	static readonly MethodologyKindDef StaleSpecKind = new("spec", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["spec"],
			[
				new WorkflowStatus("defined", "Defined", StatusKind.Open),
				new WorkflowStatus("deprecated", "Deprecated", StatusKind.TerminalCancel),
			],
			[
				new MethodologyTransitionDef("defined", "deprecated"),
			]),
	]);

	static readonly MethodologyDefinition StaleQuartet = new(
		"stale-quartet", [StaleWorkKind, StaleSpecKind]);

	static readonly MethodologyRuntime Runtime = new(StaleQuartet);

	// The production symptom, reproduced: a `work` board on a stale-materialized instance
	// must resolve `kanban`, not fall through to null/Tree, even though its own stored
	// DefaultView is null. FAILS before the fix (ResolvedKind substitutes the whole stored
	// kind-def, whose DefaultView is null, so DefaultView("work") returns null).
	[Fact]
	public void DefaultView_StaleWorkKind_FallsBackToPresetKanban() =>
		Runtime.DefaultView("work").Should().Be(BoardViewModeNames.Kanban,
			"a fully-declared but pre-field kind has no OPINION on its view — the preset's default fills the gap");

	// spec → outline + inline-lazy: both view-mode fields must merge independently from the
	// preset. FAILS before the fix for the same reason as above.
	[Fact]
	public void DefaultView_StaleSpecKind_FallsBackToPresetOutline() =>
		Runtime.DefaultView("spec").Should().Be(BoardViewModeNames.Outline);

	[Fact]
	public void OutlineReveal_StaleSpecKind_FallsBackToPresetInlineLazy() =>
		Runtime.OutlineReveal("spec").Should().Be(OutlineRevealModeNames.InlineLazy);

	// work has no OutlineReveal preset opinion either (only spec does) — the field-merge
	// preset-half for a field the matching preset also leaves null must still land on the
	// documented ultimate default (Navigate), not throw or short-circuit.
	[Fact]
	public void OutlineReveal_StaleWorkKind_DegradesToNavigate() =>
		Runtime.OutlineReveal("work").Should().Be(OutlineRevealModeNames.Navigate);

	// Everything else about the stale kind-def must keep DEFINITION-WINS-WHOLESALE semantics
	// — the per-field merge must not leak into process fields. A stale `work` kind trimmed
	// down to 3 statuses/2 transitions (unlike the real preset's 7/11) must resolve THOSE,
	// not the preset's, proving ResolvedKind (LinkConstraints/effects/workflow shape) is
	// untouched by this fix.
	[Fact]
	public void ProcessFields_StaleWorkKind_StayDefinitionScoped()
	{
		var wf = Runtime.For("work", "feature");
		wf.Should().NotBeNull();
		wf!.Statuses.Should().HaveCount(3, "the definition's own trimmed status set, not the 7-status preset");
		Runtime.LinkConstraints("work").Should().ContainSingle(c => c.Type == "feature" && c.Link == "task_spec");
	}

	// A wholly custom kind slug (no BoardKind match) must degrade gracefully: its preset
	// fallback is Simple, whose DefaultView/OutlineReveal are both null, so the overall
	// result is null/Navigate — never a throw, never an unintended borrow from an unrelated
	// preset. Covers "does the same hole exist for user-defined (non-builtin) methodologies."
	[Fact]
	public void DefaultView_UndeclaredCustomKind_DegradesToNullWithoutThrowing()
	{
		Runtime.DefaultView("totally-custom-kind-not-in-any-definition").Should().BeNull();
		Runtime.OutlineReveal("totally-custom-kind-not-in-any-definition").Should().Be(OutlineRevealModeNames.Navigate);
	}

	// A DECLARED custom kind (in the definition, but not a BoardKind name) that also omits
	// the view fields: same graceful null degradation — its preset counterpart is Simple
	// (ParseKind falls back for an unknown slug), which has no view-mode opinion either.
	[Fact]
	public void DefaultView_DeclaredCustomKind_WithoutBoardKindMatch_DegradesToNull()
	{
		var customOnly = new MethodologyDefinition("custom-only",
		[
			new MethodologyKindDef("widget", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["widget"],
					[
						new WorkflowStatus("Open", "Open", StatusKind.Open),
						new WorkflowStatus("Closed", "Closed", StatusKind.TerminalOk),
					],
					[new MethodologyTransitionDef("Open", "Closed")]),
			]),
		]);
		var runtime = new MethodologyRuntime(customOnly);

		runtime.DefaultView("widget").Should().BeNull();
		runtime.OutlineReveal("widget").Should().Be(OutlineRevealModeNames.Navigate);
	}

	// A definition that HAS opted into an explicit view (post-fix authoring, or a user who
	// deliberately chose a different default than the preset) must still win over the preset
	// — the merge is null-coalescing, not preset-always-wins.
	[Fact]
	public void DefaultView_ExplicitOverride_WinsOverPreset()
	{
		var overridden = new MethodologyDefinition("overridden",
		[
			StaleWorkKind with { DefaultView = BoardViewModeNames.Table },
		]);
		new MethodologyRuntime(overridden).DefaultView("work").Should().Be(BoardViewModeNames.Table,
			"an explicit definition value must win over the work preset's kanban");
	}
}
