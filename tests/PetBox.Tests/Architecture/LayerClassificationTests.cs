using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Mono.Cecil;
// Mono.Cecil ships its own MethodAttributes/TypeAttributes; the emitter needs the reflection ones.
using MethodAttributes = System.Reflection.MethodAttributes;
using TypeAttributes = System.Reflection.TypeAttributes;

namespace PetBox.Tests.Architecture;

// ── THE GATE OVER THE OTHER GATES ────────────────────────────────────────────────────────────────
//
// Spec `arch-gate-classification-single`, work `configtools-gate-classification`: a type must not get
// opposite classifications from two architectural gates, and the classification must be a DECISION
// declared once — not the default of whichever gate happened to look at it.
//
// The defect this file is the sensor for was invisible precisely because both gates were GREEN.
// DbLayerGuardTests called `ConfigTools` presentation; ConfigBoundaryTests called it service code;
// nothing failed, because the door `ConfigTools` actually holds (`IConfigDbFactory`, a thin typed
// facade over the guarded `IScopedDbFactory<ConfigDb>`) was not in the first gate's door list. So
// "run the gates and see" could never have found it. These four tests are what makes the NEXT one
// findable, and each fails on a different way the single declaration can come apart:
//
//   1. EveryProductType_HasOneAgreedLayer  — two rules in the table claim one type for opposite
//      layers. This is the red that the original bug would have produced: put back the old "all of
//      PetBox.Web.Mcp is presentation" rule next to the MCP-adapter rule and all twenty tool classes
//      are reported here (and both gates throw outright, because `Decide` refuses to guess).
//   2. ANewMcpModule_IsClassifiedWithoutTouchingAnyList — the classification stops being derivable
//      from shape and starts needing a list edit per type. Proven on types EMITTED AT RUNTIME, which
//      no list in this repository can contain.
//   3. BothGates_AnswerTheSameForEveryWebType — a gate stops reading the declaration and grows its
//      own definition again. The two gates are asked through their own entry points, over two
//      independent object models (reflection and Mono.Cecil).
//   4. TheClassification_ActuallyMatchesSomething — the whole table silently matches nothing and the
//      three tests above pass by vacuity. That failure mode has happened in this folder before.
public sealed class LayerClassificationTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LogTools).Assembly;

	static IEnumerable<Type> AllProductTypes() =>
		DbLayerGuardTests.ProductAssemblies.SelectMany(LayerClassification.SafeGetTypes);

	// ── 1. THE TABLE DOES NOT CONTRADICT ITSELF ──────────────────────────────────────────────────
	[Fact]
	public void EveryProductType_HasOneAgreedLayer()
	{
		var conflicts = LayerClassification.Conflicts(AllProductTypes())
			.OrderBy(c => c.Type.FullName, StringComparer.Ordinal)
			.Select(c => $"  {c.Type.FullName} <- "
				+ string.Join(" vs ", c.Rules.Select(r => $"'{r.Name}' says {r.Layer}")))
			.ToList();

		conflicts.Should().BeEmpty(
			"a type gets ONE declared classification (spec `arch-gate-classification-single`). Two rules "
			+ "of the same tier in LayerClassification.Rules claim these types for OPPOSITE layers, which "
			+ "is the same-shaped defect as work `configtools-gate-classification`: there, the two "
			+ "definitions lived in two different gate files and stayed green because neither gate could "
			+ "see what the other asserted. Do not fix this by reordering the table or by adding an "
			+ "override — decide which rule is right and delete or narrow the other. Conflicts:\n"
			+ string.Join("\n", conflicts));
	}

	// ── 2. A NEW MODULE INHERITS THE DECISION, WITH NO LIST TO EDIT ──────────────────────────────
	//
	// The acceptance criterion says the same mechanism must catch the NEXT MCP module of this shape.
	// A test over the twenty tool classes that exist today cannot show that: it would pass just as
	// well against a hard-coded list of those twenty names. So the subjects here are types EMITTED AT
	// RUNTIME — they exist only for the duration of this test, no file mentions them, and the only way
	// they can be classified is by the shape rules doing the work.
	[Fact]
	public void ANewMcpModule_IsClassifiedWithoutTouchingAnyList()
	{
		var freshAdapter = EmitMcpType("BrandNewModuleTools", tb => tb.SetCustomAttribute(
			new CustomAttributeBuilder(typeof(McpServerToolTypeAttribute).GetConstructor(Type.EmptyTypes)!, [])));

		LayerClassification.Decide(freshAdapter).Should().NotBeNull()
			.And.Subject.As<LayerRule>().Name.Should().Be("MCP module adapter",
				"a brand-new tool class is claimed by the ATTRIBUTE rule — the owner's decision reaches it "
				+ "with no line added anywhere");
		LayerClassification.Of(freshAdapter).Should().Be(Layer.Service);
		LayerClassification.PresentationCategory(freshAdapter).Should().BeNull(
			"which is the same answer both gates give for ConfigTools");

		var freshStage = EmitMcpType("BrandNewPipelineFilter", tb =>
		{
			var register = tb.DefineMethod("Register", MethodAttributes.Public | MethodAttributes.Static,
				typeof(void), [typeof(IMcpRequestFilterBuilder)]);
			register.GetILGenerator().Emit(OpCodes.Ret);
		});

		LayerClassification.Decide(freshStage).Should().NotBeNull()
			.And.Subject.As<LayerRule>().Name.Should().Be("MCP pipeline stage",
				"and a brand-new transport filter is presentation by ITS shape — the namespace is not the "
				+ "unit of classification any more, which is the half of the old rule that was wrong");
		LayerClassification.Of(freshStage).Should().Be(Layer.Presentation);

		// The same one rule decides every real tool class — reference equality, so this cannot be
		// satisfied by twenty rules that happen to share a name.
		var adapterRule = LayerClassification.Rules.Single(r => r.Name == "MCP module adapter");
		var toolClasses = LayerClassification.SafeGetTypes(Web)
			.Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false))
			.ToList();

		toolClasses.Should().HaveCountGreaterThan(15, "PetBox.Web.Mcp holds twenty tool classes");
		toolClasses.Should().OnlyContain(t => ReferenceEquals(LayerClassification.Decide(t), adapterRule),
			"ONE rule covers all of them; a per-type list is exactly what this work item forbids");
	}

	// ── 3. THE TWO GATES GIVE THE SAME ANSWER ────────────────────────────────────────────────────
	//
	// Asked through each gate's own entry point, over two independent object models: DbLayerGuardTests
	// classifies System.Type via reflection, ConfigBoundaryTests selects Mono.Cecil TypeDefinitions.
	// Those two views disagreeing on nested-type spelling or on assembly identity is a real failure
	// mode, and it is one this comparison catches rather than assumes away.
	[Fact]
	public void BothGates_AnswerTheSameForEveryWebType()
	{
		using var cecil = AssemblyDefinition.ReadAssembly(Web.Location);
		var byName = LayerClassification.SafeGetTypes(Web).ToDictionary(t => t.FullName!, StringComparer.Ordinal);

		var disagreements = new List<string>();
		foreach (var definition in cecil.MainModule.GetTypes())
		{
			if (!byName.TryGetValue(definition.FullName.Replace('/', '+'), out var reflected)) continue;

			var dbLayerGuardSaysPresentation =
				DbLayerGuardTests.Presentation(LayerClassification.Outermost(reflected)) is not null;
			var configBoundarySaysPresentation = ConfigBoundaryTests.PresentationScope.MeetsRule(definition);

			if (dbLayerGuardSaysPresentation != configBoundarySaysPresentation)
			{
				disagreements.Add($"  {definition.FullName}: DbLayerGuardTests says "
					+ $"{(dbLayerGuardSaysPresentation ? "presentation" : "service")}, ConfigBoundaryTests says "
					+ $"{(configBoundarySaysPresentation ? "presentation" : "service")}");
			}
		}

		disagreements.Should().BeEmpty(
			"two architectural gates must not classify one type oppositely (spec "
			+ "`arch-gate-classification-single`). Both read LayerClassification, so a disagreement here "
			+ "means a gate has grown a second definition of 'presentation' — a namespace literal in its "
			+ "own scope, most likely. That is how `ConfigTools` came to be presentation to one gate and "
			+ "service code to the other for as long as it did. Put the gate back on the shared "
			+ "declaration; if you disagree with what it says, change the DECLARATION. Disagreements:\n"
			+ string.Join("\n", disagreements));

		// And the specific type this work item is about, pinned by name so the answer cannot drift back
		// apart unnoticed even if the sweep above is ever narrowed.
		DbLayerGuardTests.Presentation(typeof(PetBox.Web.Mcp.ConfigTools)).Should().BeNull();
		ConfigBoundaryTests.PresentationScope
			.MeetsRule(cecil.MainModule.GetType("PetBox.Web.Mcp.ConfigTools")).Should().BeFalse();
	}

	// ── 4. GUARD-THE-GUARD ───────────────────────────────────────────────────────────────────────
	[Fact]
	public void TheClassification_ActuallyMatchesSomething()
	{
		var swept = AllProductTypes().ToList();
		swept.Should().HaveCountGreaterThan(500, "the sweep must cover the product assemblies");

		var decided = swept
			.Where(t => !t.IsNested)
			.Select(t => (Type: t, Rule: LayerClassification.Decide(t)))
			.Where(x => x.Rule is not null)
			.GroupBy(x => x.Rule!.Name)
			.ToDictionary(g => g.Key, g => g.Count());

		// Every rule in the table must claim at least one real type. A rule that matches nothing is
		// either dead or broken, and either way the assertions above are weaker than they look — the
		// exact rot that let a whole category quietly stop matching in DbLayerGuardTests once before.
		var idle = LayerClassification.Rules
			.Where(r => !decided.ContainsKey(r.Name))
			.Select(r => r.Name)
			.ToList();

		idle.Should().BeEmpty(
			"every declared rule must match at least one type in the product assemblies — a rule matching "
			+ "nothing makes the conflict check and both gates vacuous. Idle rules: "
			+ string.Join(", ", idle));

		decided.Should().ContainKey("Razor PageModel").WhoseValue.Should().BeGreaterThan(25);
		decided.Should().ContainKey("MCP module adapter").WhoseValue.Should().BeGreaterThan(15);
		decided.Should().ContainKey("MCP pipeline stage").WhoseValue.Should().BeGreaterThan(5);

		// Rule names must be unique: the conflict message names the rules that disagree, and two rules
		// sharing a name would make that message unactionable.
		LayerClassification.Rules.Select(r => r.Name).Should().OnlyHaveUniqueItems();

		// Exactly one override exists, and it is the composition root. Overrides outrank shape rules and
		// are therefore the one way to silence a genuine conflict — so their number is pinned rather
		// than left to grow into the allowlist this design replaces.
		LayerClassification.Rules.Where(r => r.Tier == RuleTier.Override).Should().ContainSingle()
			.Which.Name.Should().Be("composition root");
	}

	// A type that exists only in memory: no file names it, no allowlist can contain it, and the only
	// thing that can classify it is a shape rule.
	static Type EmitMcpType(string name, Action<TypeBuilder> shape)
	{
		var assembly = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName($"PetBox.Tests.Synthetic.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
		var module = assembly.DefineDynamicModule("Synthetic");
		var type = module.DefineType($"PetBox.Web.Mcp.{name}", TypeAttributes.Public | TypeAttributes.Class);
		shape(type);
		return type.CreateType();
	}
}
