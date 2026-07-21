namespace PetBox.Tasks.Engine.Tests;

// preset-type-order-is-load-bearing: MethodologyRuntime.DefaultType(kind) resolves the FIRST
// type of a kind's first workflow block (kind.Workflows[0].Types[0]) — declaration order in
// MethodologyPresets.cs is load-bearing, not incidental. This matters beyond "which type a
// bare quick-add gets": GuardEngine's link constraints and type-required checks key off the
// SAME default (e.g. an untyped work node is judged as its default type — "feature" — for the
// task_spec requirement, see GuardEngineLinkConstraintTests.UntypedWorkNode_IsIndictedByItsEffectiveType).
// Before this test, no test pinned the order itself — only its downstream effect on one kind
// (work). Permute any preset's Types list and this test must fail.
public sealed class MethodologyPresetTypeOrderTests
{
	// ---- presets-only shape (no stored definition — MethodologyPresets.KindDef fallback) ----

	[Theory]
	[InlineData("intake", "issue")]
	[InlineData("ideas", "idea")]
	[InlineData("spec", "spec")]
	[InlineData("work", "feature")]
	[InlineData("classic", "task")]
	[InlineData("simple", "task")]
	public void PresetsOnly_DefaultType_MatchesFirstDeclaredType(string kindSlug, string expected)
	{
		MethodologyRuntime.PresetsOnly.DefaultType(kindSlug).Should().Be(expected);
	}

	// ---- defined-kind shape (RenderBuiltinTemplate — what a real instance actually stores,
	// e.g. MethodologyInstanceService.CreateAsync from a builtin source) ----

	static readonly MethodologyRuntime QuartetInstance =
		MethodologyRuntime.From(MethodologyPresets.RenderBuiltinTemplate("quartet"));

	static readonly MethodologyRuntime ClassicInstance =
		MethodologyRuntime.From(MethodologyPresets.RenderBuiltinTemplate("classic"));

	static readonly MethodologyRuntime SimpleInstance =
		MethodologyRuntime.From(MethodologyPresets.RenderBuiltinTemplate("simple"));

	[Theory]
	[InlineData("intake", "issue")]
	[InlineData("ideas", "idea")]
	[InlineData("spec", "spec")]
	[InlineData("work", "feature")]
	public void QuartetInstance_DefaultType_MatchesFirstDeclaredType(string kindSlug, string expected)
	{
		QuartetInstance.DefaultType(kindSlug).Should().Be(expected);
	}

	[Fact]
	public void ClassicInstance_DefaultType_MatchesFirstDeclaredType()
	{
		ClassicInstance.DefaultType("classic").Should().Be("task");
	}

	[Fact]
	public void SimpleInstance_DefaultType_MatchesFirstDeclaredType()
	{
		SimpleInstance.DefaultType("simple").Should().Be("task");
	}
}
