using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// THE NAMING-CONVENTION GUARD for ModuleViewsFixture — work "test-suite-improvements-2607" pt. 3.
//
// ModuleViewsFixture (Web/ModuleViewsTests.cs) is consumed via IClassFixture by FOUR test classes —
// ModuleViewsTests (70 facts), MethodologyEditorViewsTests (21), AgentDefsAdminPageTests (16),
// MemoryStoreCostFitViewTests (2): 109 facts total. xUnit gives each class its OWN fixture instance
// (its own temp SQLite db, its own WebApplicationFactory — see the "Own ModuleViewsFixture instance"
// comments in the latter three files), so there is no runtime reset to add between classes. What
// actually holds the 109 facts together is a WRITTEN-DOWN rule, and it lives in exactly ONE of the
// four files (ModuleViewsTests.cs's fixture header): "the class only ADDS distinctly-named
// containers ... every assertion is Contains/NotContain on names no other test touches —
// accumulated state is invisible across tests." Nothing enforced "distinctly-named" before this
// guard — a new test in any of the four files that copies an existing board/store/project/instance/
// session literal collides silently with whichever sibling claimed it first, and the failure surfaces
// on the SIBLING's assertion, not on the new test's.
//
// THIS GUARD makes that convention mechanical: every entity-name literal declared via
// `const string {board|store|project|source|instance|spec|work|sessionId} = "...";` in one of the
// four consumer files must be globally unique across all four — not just within its own file. A
// human re-deriving "which names are already taken" across 109 tests spread over four files is
// exactly the kind of count this codebase has stopped trusting (DbLayerGuardTests,
// SandboxContainmentCallSiteGuardTests — three humans counted the MCP surface and got three
// different wrong numbers). A regex sweep does not get tired on test 87.
//
// WHY A TEXT SCAN AND NOT A SHARED GENERATOR: rewriting 109 tests' literals to call a generator
// would touch every assertion that echoes the name back (`html.Should().Contain("data-board-name=
// ...")`, URL string interpolation, cross-reference literals like "instance:{source}:classic"), to
// guard against a mistake only the NEXT test can make. A guard that fails a NEW collision costs one
// new file and zero churn on the 109 that already follow the rule; a generator costs touching all of
// them for the same guarantee, and the resulting names ("board-3f2a1c") would make failures harder
// to read, not easier. Naive by design, same tradeoff as the other text-scan guards in this folder —
// a guardrail against an honest next test, not a lexer defending against someone determined to evade
// it.
public sealed class ModuleViewsFixtureNamingGuardTests
{
	// The four ModuleViewsFixture consumers, relative to the tests project root.
	static readonly string[] ConsumerFiles =
	[
		"Web/ModuleViewsTests.cs",
		"Web/MethodologyEditorViewsTests.cs",
		"Web/AgentDefsAdminPageTests.cs",
		"Web/MemoryStoreCostFitViewTests.cs",
	];

	// Every local identifier NAME under which these four files currently declare an entity-name
	// literal: task board keys (`board`; `spec`/`work` in ModuleViewsTests'
	// SpecNodeDetail_HidesNonTerminalStatus_... , named after the board KIND rather than the generic
	// `board`), a methodology-instance key (`instance`), a memory-store key (`store`), a project key
	// (`project`; `source` in MethodologyEditorViewsTests' cross-project base-picker tests), and a
	// Sessions session id (`sessionId`). A new distinctly-purposed local under one of these names, or
	// a new name entirely, is a deliberate, reviewable edit to this list — TheSweep_ActuallySeesThe
	// KnownFiles below pins today's count so a silent narrowing of the pattern (one that stops
	// matching real declarations) is caught rather than passing green for the wrong reason.
	static readonly Regex EntityNameDecl = new(
		@"const\s+string\s+(?:board|store|project|source|instance|spec|work|sessionId)\s*=\s*""([^""]*)""\s*;",
		RegexOptions.Compiled);

	// `//` and `/* */` comments go; strings stay. Naive by design — see the class header.
	static string StripComments(string source)
	{
		var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
		return Regex.Replace(noBlock, @"//[^\n]*", "");
	}

	static string TestsRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "tests", "PetBox.Tests");
			if (Directory.Exists(Path.Combine(candidate, "Web"))) return candidate;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("tests/PetBox.Tests/Web not found walking up from the test bin.");
	}

	sealed record Occurrence(string File, int Line, string Value);

	static IReadOnlyList<Occurrence> SweepFile(string label, string strippedSource)
	{
		var lines = strippedSource.Split('\n');
		var found = new List<Occurrence>();
		for (var i = 0; i < lines.Length; i++)
		{
			var m = EntityNameDecl.Match(lines[i]);
			if (m.Success) found.Add(new Occurrence(label, i + 1, m.Groups[1].Value));
		}
		return found;
	}

	static IReadOnlyList<Occurrence> Sweep()
	{
		var root = TestsRoot();
		var occurrences = new List<Occurrence>();
		foreach (var rel in ConsumerFiles)
		{
			var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
			occurrences.AddRange(SweepFile(rel, StripComments(File.ReadAllText(path))));
		}
		return occurrences;
	}

	[Fact]
	public void NoTwoConsumers_ReuseTheSameEntityName()
	{
		var offenders = Sweep()
			.GroupBy(o => o.Value, StringComparer.Ordinal)
			.Where(g => g.Count() > 1)
			.Select(g => $"  \"{g.Key}\" claimed at:\n" + string.Join("\n", g.Select(o => $"    {o.File}:{o.Line}")))
			.ToList();

		offenders.Should().BeEmpty(
			"ModuleViewsFixture gives each of the four consumer classes its own db, but the convention "
			+ "documented in ModuleViewsTests.cs's fixture header ('the class only ADDS distinctly-named "
			+ "containers ... accumulated state is invisible across tests') is written in only that one "
			+ "file and enforced nowhere — a reused literal here means two tests (possibly in different "
			+ "files) now assert Contains/NotContain against the SAME name, and whichever runs second "
			+ "silently inherits the FIRST test's leftover state instead of its own. Pick a name no "
			+ "sibling test — in any of the four files — has already claimed. Offenders:\n"
			+ string.Join("\n", offenders));
	}

	// Guard the guard: the assertion above is an "is empty", vacuously green if the sweep silently
	// stopped reading the files or the pattern silently stopped matching. Same reason DbLayerGuardTests
	// and SandboxContainmentCallSiteGuardTests carry an equivalent check.
	[Fact]
	public void TheSweep_ActuallySeesTheKnownFiles()
	{
		var occurrences = Sweep();
		occurrences.Should().HaveCountGreaterThan(50,
			"the four ModuleViewsFixture consumers currently declare ~60 entity-name literals between "
			+ "them — a much lower count means the sweep stopped reading one of the files or the pattern "
			+ "stopped matching, not that the tree got smaller");

		foreach (var rel in ConsumerFiles)
			occurrences.Should().Contain(o => o.File == rel,
				$"{rel} is a known ModuleViewsFixture consumer and must contribute at least one declaration");
	}

	// The grouping logic against synthetic snippets, independent of what the four real files
	// currently contain — this is what proves the detector WORKS, not that today's tree happens to
	// be clean (the same lesson SandboxContainmentCallSiteGuardTests' Detector_* theories encode).
	[Theory]
	// Same literal, different consumer AND different local-name "kind" — still a real collision:
	// two containers under the same name in the same $system scope, whichever kind either is.
	[InlineData("const string board = \"alpha\";", "const string project = \"alpha\";", true)]
	// Distinct literals — no collision, regardless of how many times each file declares one.
	[InlineData("const string board = \"alpha\";", "const string board = \"beta\";", false)]
	// A commented-out declaration must not count as a claim.
	[InlineData("// const string board = \"alpha\";", "const string store = \"alpha\";", false)]
	public void Detector_FindsCollisionsAcrossSyntheticFiles(string fileA, string fileB, bool expectCollision)
	{
		var occurrences = SweepFile("A", StripComments(fileA)).Concat(SweepFile("B", StripComments(fileB)));
		var hasCollision = occurrences.GroupBy(o => o.Value, StringComparer.Ordinal).Any(g => g.Count() > 1);
		hasCollision.Should().Be(expectCollision, $"'{fileA}' vs '{fileB}'");
	}
}
