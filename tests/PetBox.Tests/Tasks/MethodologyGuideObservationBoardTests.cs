using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// work observations-guide-omits-utility-board: the `observations` board is a system built-in of
// kind `observation`, homed in the project's `$utility` layer and OUTSIDE every methodology
// instance — so it is nowhere in the guide's kind catalog. DeclaredKinds never names it, and
// EffectiveKinds() cannot, because MethodologyRuntime.PipelineOrder omits BoardKind.Observation on
// purpose (adding it there would change RESOLUTION and the meaning of "Other kinds this server
// knows"). The result was a guide that never mentioned the one board every project carries and
// whose findings an agent is expected to file.
//
// These pin the section that fixes it, and pin what it must NOT do: read as a kind this
// methodology declares, or duplicate the declared half.
public sealed class MethodologyGuideObservationBoardTests
{
	// What a `classic`-preset instance actually stores (the same call board provisioning makes).
	static MethodologyDefinition Classic() => MethodologyPresets.RenderPresetDefinition("classic");

	static MethodologyGuideView Guide(MethodologyDefinition def, string source = "instance") =>
		MethodologyGuide.Render(def.Name, new MethodologyRuntime(def), source, 1);

	static int Count(string haystack, string needle)
	{
		var count = 0;
		for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
			i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
			count++;
		return count;
	}

	[Fact]
	public void InstanceGuide_DocumentsTheBuiltInBoard_ItsTypeAndItsStatuses()
	{
		var md = Guide(Classic()).Markdown;

		md.Should().Contain("## Built-in board: `observations` (kind `observation`, world `$utility`)");
		// The disclaimer is the point: this is not one of the owner's methodology's kinds.
		md.Should().Contain("not part of this project's methodology");
		md.Should().Contain("outside every methodology instance");

		md.Should().Contain("- Types: observation — the only one");
		md.Should().Contain("open: seen, promoted");
		md.Should().Contain("terminal-ok (closes the node as delivered): fixed");
		md.Should().Contain("terminal-cancel (closes the node without delivery): declined");

		// The moves the server actually enforces — rendered from the preset, not hand-written.
		md.Should().Contain("seen -> promoted");
		md.Should().Contain("promoted -> fixed");
		md.Should().Contain("reason required", "the declines carry a server-enforced reason gate");
	}

	// The heading shape is load-bearing: `## Kind: <slug>` means "a kind THIS methodology
	// declares" (MethodologyGuideDeclaredKindsTests reads it that way). The built-in board must
	// not borrow it, or a project with no `observation` kind of its own would appear to have one.
	[Fact]
	public void BuiltInBoard_IsNotRenderedAsADeclaredKind()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().NotContain("## Kind: observation");
		guide.Markdown.Should().Contain("## Kind: classic");
		guide.Invariants.Should().NotContain(i => i.Kind == "observation",
			"MethodologyInvariant has no channel to mark a rule \"not from your methodology\"");
	}

	[Fact]
	public void InstanceGuide_StillHasExactlyOneKindSectionPerDeclaredKind()
	{
		var guide = Guide(Classic());

		Count(guide.Markdown, "## Kind:").Should().Be(1, "classic declares exactly one kind");
		Count(guide.Markdown, "## Kind: classic").Should().Be(1);
		Count(guide.Markdown, "## Built-in board: `observations`").Should().Be(1,
			"the built-in board renders ONCE, not once per catalog reading");
	}

	// The no-instance case renders the full builtin catalog (EffectiveKinds — which still omits
	// observation, by design); the board must appear there too, and still not as a declared kind.
	[Fact]
	public void NoDeclaredKinds_StillRendersTheBoard_OnceAndOutsideTheKindCatalog()
	{
		var guide = MethodologyGuide.Render("presets", MethodologyRuntime.PresetsOnly, "presets", null);
		var md = guide.Markdown;

		md.Should().Contain("## Built-in board: `observations` (kind `observation`, world `$utility`)");
		Count(md, "## Built-in board: `observations`").Should().Be(1);
		md.Should().NotContain("## Kind: observation",
			"Observation is deliberately absent from MethodologyRuntime.PipelineOrder");
		md.Should().Contain("## Kind: work", "the full builtin catalog still renders");
	}
}
