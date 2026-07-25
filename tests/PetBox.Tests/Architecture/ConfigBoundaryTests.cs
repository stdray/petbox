using System.Reflection;
using NetArchTest.Rules;

namespace PetBox.Tests.Architecture;

// The presentation-layer boundary for PetBox.Config: a Razor page must reach config through
// IConfigDirectory (THE service layer — see ConfigDirectory.cs), never by holding
// IConfigDbFactory and opening a ConfigDb itself.
//
// Why a test and not a review note: this exact boundary was declared fixed once before, on a
// branch that never reached main, and the violations quietly lived on for six weeks because
// nothing in the build disagreed. A page that re-acquires the factory now fails the build.
//
// ConfigTools (the MCP adapter) and DeployAgentService are deliberately NOT in scope — they are
// module/service code, not presentation, and they own their data access the same way
// LogCatalogTools owns the log catalog.
public sealed class ConfigBoundaryTests
{
	static readonly Assembly Web = typeof(PetBox.Web.Mcp.LogTools).Assembly;

	// The CONNECTION doors — a factory and the context it hands out. Row records (ConfigBinding,
	// ConfigBindingHistoryEntry, TagVocabularyEntry) are deliberately absent: those are DTOs the
	// service layer legitimately returns, and banning them would forbid rendering config at all.
	static readonly string[] ConfigDoors =
	[
		"PetBox.Config.Data.IConfigDbFactory",
		"PetBox.Config.Data.ConfigDb",
	];

	[Fact]
	public void WebPages_DoNotTouch_ConfigDbOrFactory()
	{
		var result = Types.InAssembly(Web)
			.That().ResideInNamespaceStartingWith("PetBox.Web.Pages")
			.Should().NotHaveDependencyOnAny(ConfigDoors)
			.GetResult();

		result.IsSuccessful.Should().BeTrue(
			"a Razor page must go through IConfigDirectory, not open ConfigDb itself; offenders: "
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
}
