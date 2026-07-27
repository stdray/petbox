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
// guard — a new test that copies a board/store/project/instance/session literal already claimed by
// another test IN ITS OWN CLASS collides silently, and the failure surfaces on the SIBLING's
// assertion, not on the new test's.
//
// THIS GUARD makes that convention mechanical: every entity-name literal declared via
// `const string {board|store|project|source|instance|spec|work|sessionId} = "...";` must be unique
// WITHIN its own consumer file. Deliberately not across files: the per-class fixture instance means
// two consumers never share a db, so the same literal in two of them cannot collide, and flagging it
// would be a false constraint on an author who did nothing wrong. A
// human re-deriving "which names are already taken" across 70 tests in one file is
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
	public void NoTwoTestsInAConsumer_ReuseTheSameEntityName()
	{
		var offenders = Sweep()
			.GroupBy(o => (o.File, o.Value))
			.Where(g => g.Count() > 1)
			.Select(g => $"  \"{g.Key.Value}\" claimed twice in {g.Key.File} at lines "
				+ string.Join(", ", g.Select(o => o.Line)))
			.ToList();

		offenders.Should().BeEmpty(
			"a consumer declares IClassFixture<ModuleViewsFixture>, so xUnit builds the fixture ONCE per "
			+ "class and never resets it between that class's tests — the containers one test creates are "
			+ "still there for the next. The convention that keeps that safe ('the class only ADDS "
			+ "distinctly-named containers ... accumulated state is invisible across tests') is written in "
			+ "ModuleViewsTests.cs's fixture header alone; two tests in the SAME file claiming one literal "
			+ "means whichever runs second asserts Contains/NotContain against its neighbour's leftovers. "
			+ "Deliberately per-file: each class gets its own fixture instance and its own db "
			+ "(petbox-modviews-<guid> + NewTempConnectionString), so the SAME literal in two DIFFERENT "
			+ "consumers cannot collide and is not an offence here. Offenders:\n"
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
	// Same literal twice in ONE file under different local-name "kinds" — a real collision: both
	// tests hit the same never-reset fixture db, whichever kind either container is.
	[InlineData("const string board = \"alpha\";\nconst string project = \"alpha\";", "", true)]
	// Distinct literals in one file — no collision, however many each file declares.
	[InlineData("const string board = \"alpha\";\nconst string board = \"beta\";", "", false)]
	// A commented-out declaration must not count as a claim.
	[InlineData("// const string board = \"alpha\";\nconst string store = \"alpha\";", "", false)]
	// The SAME literal in two DIFFERENT consumers is legal — separate fixture instances, separate
	// dbs. This case is what pins the guard to per-file scope; grouping by value alone fails it.
	[InlineData("const string board = \"alpha\";", "const string board = \"alpha\";", false)]
	public void Detector_FindsCollisionsWithinAFileButNotAcrossFiles(string fileA, string fileB, bool expectCollision)
	{
		var occurrences = SweepFile("A", StripComments(fileA)).Concat(SweepFile("B", StripComments(fileB)));
		var hasCollision = occurrences.GroupBy(o => (o.File, o.Value)).Any(g => g.Count() > 1);
		hasCollision.Should().Be(expectCollision, $"'{fileA}' vs '{fileB}'");
	}
}
