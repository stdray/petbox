using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// The presentation-layer boundary for PetBox.Config: presentation code must reach config through
// IConfigDirectory (THE service layer — see ConfigDirectory.cs), never by holding IConfigDbFactory
// and opening a ConfigDb itself.
//
// Why a test and not a review note: this exact boundary was declared fixed once before, on a
// branch that never reached main, and the violations quietly lived on for six weeks because
// nothing in the build disagreed. A page that re-acquires the factory now fails the build.
//
// WHAT "PRESENTATION" MEANS HERE IS NOT DECIDED IN THIS FILE. This gate used to scope itself with a
// namespace literal of its own (`PetBox.Web.Pages`) and then explain in prose that ConfigTools was
// "deliberately NOT in scope — module/service code, not presentation". Ten lines away in
// DbLayerGuardTests, the same type was classified as presentation. Two gates, opposite answers, both
// green — the bug this file's scope now exists to make unrepeatable (work
// `configtools-gate-classification`). The scope is `LayerClassification.PresentationTypesOf`, the one
// declaration both gates read, so the ConfigTools exclusion is a CONSEQUENCE of the decision rather
// than a comment restating it, and DeployAgentService is out of scope for the same reason: it is
// service code by that same table.
//
// Reading the declaration also WIDENED this gate. It used to watch Razor pages only; it now watches
// every presentation type in the Web assembly — pages, the MCP transport pipeline, middleware, page
// filters and the minimal-API endpoint classes — because that is what the declaration says
// presentation is. DeployApi keeps its own test below: it is pinned BY NAME, so that the day it stops
// matching the endpoint-class shape the assertion fails loudly instead of silently covering nothing.
public sealed class ConfigBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LogTools).Assembly;

	// THE SCOPE, READ FROM THE DECLARATION. Exposed rather than inlined so
	// `LayerClassificationTests.BothGates_AnswerTheSameForEveryWebType` can run the real instance this
	// gate uses against the reflective gate's answer — comparing the two gates, not two copies of one
	// idea.
	internal static readonly LayerClassification.PresentationTypesOf PresentationScope = new(Web);

	// The CONNECTION doors — a factory and the context it hands out. Row records (ConfigBinding,
	// ConfigBindingHistoryEntry, TagVocabularyEntry) are deliberately absent: those are DTOs the
	// service layer legitimately returns, and banning them would forbid rendering config at all.
	static readonly string[] ConfigDoors =
	[
		"PetBox.Config.Data.IConfigDbFactory",
		"PetBox.Config.Data.ConfigDb",
	];

	[Fact]
	public void PresentationTypes_DoNotTouch_ConfigDbOrFactory()
	{
		var result = Types.InAssembly(Web)
			.That().MeetCustomRule(PresentationScope)
			.Should().NotHaveDependencyOnAny(ConfigDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"presentation code must go through IConfigDirectory, not open ConfigDb itself; the set of "
			+ "presentation types comes from LayerClassification, so if you disagree with a type being in "
			+ "scope, change the DECLARATION (and see whether the other gate still agrees) rather than "
			+ "carving an exception here; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	[Fact]
	public void DeployApi_DoesNotTouch_ConfigDbOrFactory()
	{
		var result = Types.InAssembly(Web)
			.That().HaveName("DeployApi")
			.Should().NotHaveDependencyOnAny(ConfigDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"the deploy REST surface must go through IConfigDirectory; offenders: "
			+ string.Join(", ", result.FailingTypeNames ?? []));
	}

	// Guard-the-guard: `MeetCustomRule` selecting NOTHING would make the assertion above pass by
	// vacuity, and a Cecil/reflection name mismatch (`Outer/Nested` vs `Outer+Nested`) is exactly the
	// kind of silent emptying that produces it. Both halves are checked: the scope is populated, and
	// it really does contain the pages this gate was originally written for.
	[Fact]
	public void TheScope_ActuallySelectsThePresentationTypes()
	{
		PresentationScope.Count.Should().BeGreaterThan(25,
			"the Web assembly's presentation set is Pages/** plus the pipeline — an empty or tiny scope "
			+ "means the classification stopped matching and this gate is green over nothing");

		var selected = Types.InAssembly(Web)
			.That().MeetCustomRule(PresentationScope)
			.GetTypes()
			.Select(t => t.FullName)
			.ToList();

		selected.Should().Contain("PetBox.Web.Pages.Admin.ProjectDetailModel",
			"the Cecil-side scope must resolve the same types the reflective classifier does");
		selected.Should().NotContain("PetBox.Web.Mcp.ConfigTools",
			"and it must NOT contain the type this whole work item is about — it is service code by the "
			+ "shared declaration, which is the same answer DbLayerGuardTests now gives");
	}
}
