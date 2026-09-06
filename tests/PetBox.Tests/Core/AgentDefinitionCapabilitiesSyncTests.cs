using System.Text.RegularExpressions;
using PetBox.Core.Contract;

namespace PetBox.Tests.AgentDefs;

// AgentDefinitionCapabilities.All mirrors src/clients-ts/petbox-wire/src/harness-capabilities.ts's
// CAPABILITIES — kit data the C# server cannot import at runtime (it ships with the npm package,
// not the server assembly), so a single hand-synced C# copy is the best available without a codegen
// pipeline (same tradeoff UiStateTypeSyncTests documents for BrowserState/ui-state.ts). This test is
// the enforcement: it reads the ACTUAL .ts source and fails loudly the moment the two lists drift,
// so "no second hardcoded copy" holds as a build-time guarantee, not a comment someone has to
// remember to honor.
//
// The C# list is not a decorative copy: DefaultAgentDefinition.Validate refuses a
// requiredCapabilities value on the shipped baseline that is not in it (proven by
// DefaultAgentDefinitionTests.Validate_RejectsACapabilityNoHarnessDeclares). So this test and that
// one are two halves of one chain — harness-capabilities.ts ⇄ AgentDefinitionCapabilities ⇄
// src/common/default-agents.json — and a typo anywhere along it goes red in CI rather than at wire
// time on a user's machine.
public sealed partial class AgentDefinitionCapabilitiesSyncTests
{
	static string RepoRootTsFile()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src", "clients-ts", "petbox-wire", "src", "harness-capabilities.ts");
			if (File.Exists(candidate)) return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/clients-ts/petbox-wire/src/harness-capabilities.ts not found walking up from the test bin.");
	}

	[GeneratedRegex(
		@"export const CAPABILITIES: readonly Capability\[\] = \[(?<body>[^\]]*)\]",
		RegexOptions.Singleline)]
	private static partial Regex CapabilitiesArrayRegex();

	[GeneratedRegex("\"([a-z_]+)\"")]
	private static partial Regex QuotedIdRegex();

	static List<string> ParseCapabilitiesArray(string tsSource)
	{
		var match = CapabilitiesArrayRegex().Match(tsSource);
		match.Success.Should().BeTrue("harness-capabilities.ts must still declare `export const CAPABILITIES: readonly Capability[] = [...]`");
		return QuotedIdRegex().Matches(match.Groups["body"].Value).Select(m => m.Groups[1].Value).ToList();
	}

	[Fact]
	public void KnownCapabilities_MatchTheKitsHarnessCapabilitiesTs()
	{
		var tsIds = ParseCapabilitiesArray(File.ReadAllText(RepoRootTsFile()));

		AgentDefinitionCapabilities.All.Should().Equal(tsIds,
			"AgentDefinitionCapabilities.All (the server-side catalog) must list exactly the same " +
			"capability ids, in the same order, as harness-capabilities.ts's CAPABILITIES — update both " +
			"together, this test is the only thing that would otherwise notice a drift");
	}

	// The parser itself, proven against a synthetic snippet independent of the real file's prose —
	// so a failure above means an actual catalog drift, not a regex that stopped matching real TS.
	[Fact]
	public void ParseCapabilitiesArray_ExtractsIdsInOrder()
	{
		const string snippet = """
			export type Capability = "a" | "b";
			export const CAPABILITIES: readonly Capability[] = [
			  "a",
			  "b",
			] as const;
			""";

		ParseCapabilitiesArray(snippet).Should().Equal("a", "b");
	}
}
