using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// board-view-modes / methodology-default-view-field: the view-mode name vocabulary
// (BoardViewModeNames) and MethodologyRuntime.DefaultView's per-kind resolution — definition
// override wins when the project declares the kind, else the builtin preset's own default
// (work→kanban, spec→outline, intake→table, ideas→tree; simple/classic declare none, so a
// board of those kinds has no methodology-level preference and BoardViewModeRegistry.Resolve
// falls all the way to the hardcoded Tree default).
public sealed class BoardViewModeNamesTests
{
	[Theory]
	[InlineData(BoardViewModeNames.Tree)]
	[InlineData(BoardViewModeNames.Tags)]
	[InlineData(BoardViewModeNames.Kanban)]
	[InlineData(BoardViewModeNames.Outline)]
	[InlineData(BoardViewModeNames.Table)]
	public void IsKnown_EveryDeclaredName_True(string name) =>
		BoardViewModeNames.IsKnown(name).Should().BeTrue();

	[Theory]
	[InlineData("bogus")]
	[InlineData("")]
	[InlineData(null)]
	public void IsKnown_UnknownOrAbsent_False(string? name) =>
		BoardViewModeNames.IsKnown(name).Should().BeFalse();

	[Theory]
	[InlineData("work", "kanban")]
	[InlineData("spec", "outline")]
	[InlineData("intake", "table")]
	[InlineData("ideas", "tree")]
	public void PresetKinds_DeclareTheirAssignedDefaultView(string kindSlug, string expected) =>
		MethodologyRuntime.PresetsOnly.DefaultView(kindSlug).Should().Be(expected);

	[Theory]
	[InlineData("simple")]
	[InlineData("classic")]
	public void PresetKinds_WithNoAssignedView_DefaultToNull(string kindSlug) =>
		MethodologyRuntime.PresetsOnly.DefaultView(kindSlug).Should().BeNull();

	[Fact]
	public void UnknownKindSlug_ReadsAsSimple_NullDefaultView() =>
		MethodologyRuntime.PresetsOnly.DefaultView("no-such-kind").Should().BeNull();

	[Fact]
	public void DefinedKind_OverridesThePresetDefaultView()
	{
		// A project-defined "work" kind overrides the PRESET's own defaultView (kanban),
		// same merge semantics as every other per-kind resolver on MethodologyRuntime.
		var def = new MethodologyDefinition("proj",
		[
			new MethodologyKindDef("work", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["task"],
					[new("Todo", "Todo", StatusKind.Open), new("Done", "Done", StatusKind.TerminalOk)],
					[new("Todo", "Done")]),
			])
			{
				DefaultView = BoardViewModeNames.Table,
			},
		]);
		var runtime = MethodologyRuntime.From(def);

		runtime.DefaultView("work").Should().Be(BoardViewModeNames.Table);
		// A kind the definition does NOT declare still falls through to its own preset.
		runtime.DefaultView("spec").Should().Be(BoardViewModeNames.Outline);
	}

	[Fact]
	public void DefinedKind_WithoutDefaultView_IsValidAndNull()
	{
		// methodology-default-view-field: an old-shape definition (or one that simply never
		// sets this optional field) is valid — DefaultView reads null, same as a preset with
		// no assigned view (simple/classic).
		var def = new MethodologyDefinition("proj",
		[
			new MethodologyKindDef("support", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["ticket"],
					[new("New", "New", StatusKind.Open), new("Done", "Done", StatusKind.TerminalOk)],
					[new("New", "Done")]),
			]),
		]);
		MethodologyRuntime.From(def).DefaultView("support").Should().BeNull();
	}
}
