using PetBox.Tasks.Contract;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// guide-declared-kinds: the process guide renders the kinds a project's methodology
// DECLARES, never MethodologyRuntime.EffectiveKinds. The live defect this pins: a project
// running the one-kind `classic` preset got a guide signed "source: instance" under the
// heading "How to work this project's boards" that carried the whole quartet — intake,
// ideas, spec, work, simple — with their gates, their transition effects, the work→spec
// auto-wire and the `links.idea_spec`/`links.task_spec` creation requirements, for boards
// the project does not have and nodes it cannot create.
//
// The merge itself stays: EffectiveKinds is a RESOLUTION set (a board of any kind must
// resolve, declared or not) and every resolver still reads it —
// UndeclaredKind_StillResolves_EffectiveKindsUnchanged is that promise's guard.
public sealed class MethodologyGuideDeclaredKindsTests
{
	// What a `classic`-preset instance actually stores: RenderPresetDefinition is the same
	// call board provisioning makes, so this is the production document, not a stand-in.
	static MethodologyDefinition Classic() => MethodologyPresets.RenderPresetDefinition("classic");

	static MethodologyGuideView Guide(MethodologyDefinition def, string source = "instance") =>
		MethodologyGuide.Render(def.Name, new MethodologyRuntime(def), source, 1);

	// A kind the instance declares ITSELF, on top of a builtin preset's kinds — the "does the
	// guide still carry everything mine" half of the regression.
	static readonly MethodologyKindDef WikiKind = new("wiki", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["page"],
			[
				new WorkflowStatus("draft", "Draft", StatusKind.Open),
				new WorkflowStatus("published", "Published", StatusKind.TerminalOk),
			],
			[new MethodologyTransitionDef("draft", "published")]),
	]);

	[Fact]
	public void ClassicInstance_RendersItsOwnKindOnly_NoForeignRules()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().Contain("## Kind: classic");
		foreach (var foreign in new[] { "intake", "ideas", "spec", "work", "simple" })
			guide.Markdown.Should().NotContain($"## Kind: {foreign}", "the project has no board of that kind");

		// The rules that misled a live agent: quartet link requirements, the quartet's
		// cross-board automation, and roll-ups keyed on boards this project does not have.
		guide.Markdown.Should().NotContain("must carry a `task_spec` link");
		guide.Markdown.Should().NotContain("must carry a `idea_spec` link");
		guide.Markdown.Should().NotContain("### Auto-wire");
		guide.Markdown.Should().NotContain("### Delivery roll-up");
		guide.Markdown.Should().NotContain("### Transition effects");

		// The machine half matters most: MethodologyInvariant has no "not yours" channel, so a
		// foreign invariant reaches a downstream consumer as a rule of THIS project.
		guide.Invariants.Should().OnlyContain(i => i.Kind == "classic");
		guide.Invariants.Should().Contain(new MethodologyInvariant("classic", "approval_gate", "Review -> Done"));
	}

	[Fact]
	public void ClassicInstance_NamesTheUndeclaredKinds_WithoutTheirRules()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().Contain("## Other kinds this server knows");
		guide.Markdown.Should().Contain("intake, ideas, spec, work, simple");
		guide.Markdown.Should().Contain("NONE of their rules apply here");
	}

	// custom-kind-route-undiscoverable: the guide must not read as a closed world of server
	// presets — a project can always author its own kind. This line names the two verbs
	// (tasks_methodology_utility_upsert / tasks_methodology_rules_upsert) and must appear
	// whether or not "Other kinds this server knows" itself rendered — see the sibling
	// assertion on NoDeclaredKinds_RenderTheFullBuiltinCatalog_HeadedAsDefaults below for the
	// case where that section is absent.
	[Fact]
	public void ClassicInstance_NamesTheExtensibilityVerbs()
	{
		var guide = Guide(Classic());

		guide.Markdown.Should().Contain("## Declaring your own kind");
		guide.Markdown.Should().Contain("tasks_methodology_utility_upsert");
		guide.Markdown.Should().Contain("tasks_methodology_rules_upsert");
	}

	[Fact]
	public void InstanceGuide_KeepsTheProjectHeading()
	{
		Guide(Classic()).Markdown.Should().Contain("How to work this project's boards");
		Guide(Classic()).Markdown.Should().NotContain("NO methodology is chosen");
	}

	// Regression: an instance loses NOTHING it declares — the quartet's four kinds plus a kind
	// the instance added itself all render in full, invariants included.
	[Fact]
	public void QuartetInstance_KeepsEveryDeclaredKind_IncludingItsOwnAdditions()
	{
		var quartet = MethodologyPresets.RenderPresetDefinition("quartet");
		var guide = Guide(quartet with { Kinds = [.. quartet.Kinds, WikiKind] });

		foreach (var own in new[] { "intake", "ideas", "spec", "work", "wiki" })
			guide.Markdown.Should().Contain($"## Kind: {own}");

		guide.Markdown.Should().Contain("must carry a `task_spec` link", "work IS declared here — its own rules stay");
		guide.Markdown.Should().Contain("### Auto-wire");
		guide.Markdown.Should().Contain("### Delivery roll-up");
		guide.Invariants.Should().Contain(new MethodologyInvariant("work", "approval_gate", "Review -> Done"));
		guide.Invariants.Should().Contain(new MethodologyInvariant("spec", "delivery", "required:feature; defects:bug"));

		// Only the kinds it does NOT declare are demoted to names.
		guide.Markdown.Should().NotContain("## Kind: classic");
		guide.Markdown.Should().NotContain("## Kind: simple");
		guide.Markdown.Should().Contain("classic, simple — built-in preset kinds");
		guide.Invariants.Should().NotContain(i => i.Kind == "classic" || i.Kind == "simple");
	}

	// Regression: a project with NO methodology instance still gets the whole builtin catalog
	// (source "presets") — there the catalog IS what its boards resolve against — but headed
	// honestly as defaults nobody chose, not as "this project's boards".
	[Fact]
	public void NoDeclaredKinds_RenderTheFullBuiltinCatalog_HeadedAsDefaults()
	{
		var guide = MethodologyGuide.Render(MethodologyPresets.Name, MethodologyRuntime.PresetsOnly, "presets", null);

		foreach (var kind in new[] { "intake", "ideas", "spec", "work", "classic", "simple" })
			guide.Markdown.Should().Contain($"## Kind: {kind}");
		guide.Markdown.Should().Contain("NO methodology is chosen for this project");
		guide.Markdown.Should().NotContain("How to work this project's boards");
		guide.Markdown.Should().NotContain("## Other kinds this server knows", "nothing is undeclared when everything is a default");
		// custom-kind-route-undiscoverable: extensibility is NOT nested inside "Other kinds"
		// (which legitimately renders nothing here) — it must still tell the reader a new kind
		// can be authored, and by which verbs.
		guide.Markdown.Should().Contain("## Declaring your own kind");
		guide.Markdown.Should().Contain("tasks_methodology_utility_upsert");
		guide.Markdown.Should().Contain("tasks_methodology_rules_upsert");
		guide.Invariants.Should().Contain(new MethodologyInvariant("work", "approval_gate", "Review -> Done"));
		guide.Source.Should().Be("presets");
	}

	// THE thing this change must not break: EffectiveKinds is the resolution merge and stays
	// exactly as it was — only the guide stopped reading it. A board of a kind the definition
	// does not declare resolves its preset workflow, types, link constraints and auto-wire.
	[Fact]
	public void UndeclaredKind_StillResolves_EffectiveKindsUnchanged()
	{
		var runtime = new MethodologyRuntime(Classic());

		runtime.DeclaredKinds.Select(k => k.Kind).Should().Equal("classic");
		runtime.EffectiveKinds().Select(k => k.Kind)
			.Should().Equal("classic", "intake", "ideas", "spec", "work", "simple");

		runtime.IsDefinedKind("ideas").Should().BeFalse("the classic definition declares no ideas kind");
		var ideas = runtime.For("ideas", "idea");
		ideas.Should().NotBeNull("a board of an undeclared kind must still resolve its FSM");
		ideas!.Statuses.Should().Contain(s => s.Slug == "accepted");
		runtime.DefaultType("ideas").Should().Be("idea");
		runtime.Blocks("ideas").Should().NotBeEmpty();

		runtime.For("work", "feature").Should().NotBeNull();
		runtime.LinkConstraints("work").Should().NotBeEmpty("the preset's process rules still bind a work board here");
		runtime.AutoWireFrom("work").Should().Be("spec");
		runtime.DeliveryOf("spec").Should().NotBeNull();
	}
}
