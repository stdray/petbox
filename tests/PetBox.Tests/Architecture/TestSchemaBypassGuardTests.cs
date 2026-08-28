using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// THE GUARD for the TestSchema template-copy mechanism (work `test-schema-templates-all-tiers`):
//
//     Every tier's schema build in tests/ goes through TestSchema.{Core,Tasks,Memory,Sessions,Log,
//     Config,Deploy} — the lazy-build-once-per-process template + File.Copy (see TestSchema.cs) —
//     not through the tier's real production *Schema.Ensure or the raw MigrationRunner.Run. A test
//     that calls the production entry point directly re-runs FluentMigrator's full migration set on
//     every instantiation (xUnit news a fresh test-class instance per test, so a plain IDisposable
//     fixture pays that cost on EVERY test), which is exactly the cost the template exists to
//     amortize.
//
// THIS IS A TEXT SCAN, like DbLayerGuardTests' service-locator plane (see that file for the fuller
// rationale): what it looks for is a literal CALL SITE, not a type relationship reflection can see —
// TasksSchema.Ensure is just a static method reference or invocation, indistinguishable from any
// other by its type. Same false-green risk applies: the pattern also matches inside a comment. If
// you want to talk about the real Ensure/MigrationRunner.Run without tripping the scan, describe it
// in prose rather than spelling `Foo.Ensure(` or `MigrationRunner.Run(` verbatim — this file's own
// doc comments do exactly that above.
public sealed class TestSchemaBypassGuardTests
{
	// Every real schema entry point a test must reach only through TestSchema's copy-of-a-template
	// wrapper. The optional namespace-qualified prefix matters: a fully-qualified call
	// (`PetBox.Tasks.Data.TasksSchema.Ensure(cs)`) is exactly how GoldenSchemaTests and
	// LegacySchemaAdoptionTests spell it, and a scan that only matched the bare name would miss it —
	// DbLayerGuardTests' service-locator pattern shipped that exact hole once (see its own comment).
	static readonly Regex DirectSchemaCall = new(
		@"\b(?:PetBox\.Tasks\.Data\.)?TasksSchema\.Ensure\b"
		+ @"|\b(?:PetBox\.Memory\.Data\.)?MemorySchema\.Ensure\b"
		+ @"|\b(?:PetBox\.Sessions\.Data\.)?SessionsSchema\.Ensure\b"
		+ @"|\b(?:PetBox\.Log\.Core\.Data\.)?LogSchema\.Ensure\b"
		+ @"|\b(?:PetBox\.Config\.Data\.)?ConfigSchema\.Ensure\b"
		+ @"|\b(?:PetBox\.Deploy\.Data\.)?DeploySchema\.Ensure\b"
		+ @"|\bMigrationRunner\.Run\(",
		RegexOptions.Compiled);

	// ── THE ALLOWLIST — every entry earns its keep by DELIBERATELY testing the real schema/migration
	// mechanism itself, not by building a fixture the cheap way. Swapping any of these to TestSchema.*
	// would either make the test pass for the wrong reason or stop testing what it claims to test.
	//
	// NEVER add an entry to make a new violation pass — an ordinary test that just needs "a tasks db"
	// or "a memory db" belongs on TestSchema.*, full stop. An entry here is a test whose SUBJECT is
	// the migration/bootstrap mechanism, which is a narrow and easily-named set.
	static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["TestSchema.cs"] =
			"the implementation itself — it calls each tier's real Ensure/MigrationRunner.Run exactly " +
			"once per process to BUILD the template every other test copies.",
		["CoreDbWalTests.cs"] =
			"asserts that MigrationRunner.Run (the REAL bootstrap) leaves core.db in WAL — routing " +
			"through TestSchema.Core would prove nothing about the bootstrap path this test exists to pin.",
		["LogSchemaWalTests.cs"] =
			"asserts LogSchema.Ensure's own WAL-conversion behavior, including simulating the OLD " +
			"pre-fix bootstrap it must upgrade an existing file FROM — needs the real calls, a template " +
			"copy shares no code path with the conversion being tested.",
		["ConfigSchemaWalTests.cs"] =
			"asserts ConfigSchema.Ensure's own WAL-conversion behavior, including simulating the OLD " +
			"pre-WAL bootstrap it must upgrade an existing populated file FROM — the exact sibling of " +
			"LogSchemaWalTests above, and for the same reason: a template copy shares no code path " +
			"with the conversion being tested.",
		["GoldenSchemaTests.cs"] =
			"snapshots the SHAPE each tier's real migration set produces on a clean db — the whole " +
			"point is running the real Ensure/MigrationRunner.Run and inspecting the result, not " +
			"starting from a pre-built copy.",
		["LegacySchemaAdoptionTests.cs"] =
			"feeds hand-rolled legacy DDL into the real ConfigSchema.Ensure/LogSchema.Ensure to prove " +
			"the baseline migration ADOPTS a pre-migration file — a template copy shares no code path " +
			"with that adoption logic.",
		["TemporalSchemaInvariantsTests.cs"] =
			"calls the real TasksSchema.Ensure/MemorySchema.Ensure TWICE on the same file to prove " +
			"Ensure itself is idempotent — TestSchema's OWN idempotency (skip-if-file-exists) would " +
			"mask a regression in the very thing under test.",
		["KqlTestHost.cs"] =
			"seeds an in-memory, shared-cache sqlite connection string (mode=memory) — TestSchema's " +
			"File.Copy has no filesystem path to copy to or from there.",
		["LegacyMigrationTests.cs"] =
			"hand-writes an M001-era plan_nodes file and calls Ensure to prove M002..M004 upgrade it " +
			"in place — the file already EXISTS and is deliberately BEHIND, so TestSchema.Tasks's " +
			"file-exists-skip would never run the migration this test exists to pin.",
		["NodeCommitsTests.cs"] =
			"one test (Migration_SeedsCommitsFromLegacyCommitRef_AndDropsColumn) hand-writes an " +
			"M001-era file with a legacy CommitRef column and calls Ensure to prove M002..M011 migrate " +
			"it — same already-exists-but-behind shape as LegacyMigrationTests; every other test in " +
			"this file uses TestSchema.Tasks normally via the class's own ScopedDbFactory.",
		["NodeDecisionFlagAndProvenanceTests.cs"] =
			"one test (Migration_UpgradesAFileThatAlreadyHasNodes_WithoutTouchingTheirData) hand-writes " +
			"an M001-era file holding real nodes and calls Ensure to prove M021 ALTERs it in place — " +
			"the same already-exists-but-behind shape as LegacyMigrationTests/NodeCommitsTests, and the " +
			"one thing TestSchema.Tasks structurally cannot show (its template is built from the " +
			"CURRENT schema, so nothing is ever upgraded). Every other test in this file uses " +
			"TestSchema.Tasks normally via the class fixture's ScopedDbFactory.",
		["MemoryStoreMergeTests.cs"] =
			"M010's legacy-store merge scans SIBLING per-store files in the same directory at ensure " +
			"time; the seeded legacy files live next to the project db BEFORE it is first ensured, so " +
			"TestSchema.Memory's template (built once, in an empty directory) would never observe them.",
		["VectorizationJobResilienceTests.cs"] =
			"one test (Drain_migrates_a_project_file_left_behind_by_an_old_schema) deliberately rolls " +
			"a file's schema BACKWARD (drops the search tables, deletes a VersionInfo row) then expects " +
			"the drain job's next ensure to migrate it forward — the file already exists at that point, " +
			"so TestSchema.Memory would skip it; only the real Ensure's MigrateUp can heal it.",
		["SessionsSearchCursorMigrationTests.cs"] =
			"the whole class exists to pin M007's ADOPTION of a live pre-M007 file (search_cursor/" +
			"search_deadletter created at runtime by SessionFactsJob, with VersionInfo never told about " +
			"M007) — SimulatePreM007Production hand-rewinds an already-ensured file, so a later " +
			"TestSchema.Sessions call would see it exists and skip the very migration under test.",
	};

	static string TestsDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "tests", "PetBox.Tests");
			if (Directory.Exists(candidate)) return candidate;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("tests/PetBox.Tests not found walking up from the test bin.");
	}

	// Every test .cs file, minus build artifacts (bin/obj hold generated copies that would be scanned
	// twice), mirroring DbLayerGuardTests.ProductSourceFiles.
	static IReadOnlyList<string> TestSourceFiles() =>
		[.. Directory.EnumerateFiles(TestsDir(), "*.cs", SearchOption.AllDirectories)
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

	// This file's OWN name — excluded by construction, not allowlisted. Its assertions and doc
	// comments necessarily SPELL every pattern the scan looks for (that's how TheGuard_ActuallyInspectsSomething
	// pins the regex), so it would otherwise match itself on every run. Same idea as DbLayerGuardTests
	// excluding Program.cs by name rather than adding it to an allowlist that means "this is debt".
	const string GuardFile = "TestSchemaBypassGuardTests.cs";

	static IReadOnlyList<string> MatchingFiles() =>
		[.. TestSourceFiles()
			.Where(p => !string.Equals(Path.GetFileName(p), GuardFile, StringComparison.OrdinalIgnoreCase))
			.Where(p => DirectSchemaCall.IsMatch(File.ReadAllText(p)))];

	[Fact]
	public void NoTestBypassesTheSchemaTemplate()
	{
		var offenders = MatchingFiles()
			.Select(Path.GetFileName)
			.Where(f => !Allowlist.ContainsKey(f!))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

		offenders.Should().BeEmpty(
			"tests/ must build a tier's schema through TestSchema.{Core,Tasks,Memory,Sessions,Log,"
			+ "Config,Deploy} (the lazy-build-once-per-process template + File.Copy — see TestSchema.cs), "
			+ "not through the tier's real *Schema.Ensure or MigrationRunner.Run directly. A direct call "
			+ "re-runs the full FluentMigrator migration set on every test-class instantiation, which is "
			+ "exactly the cost the template exists to amortize. If this file's subject genuinely IS the "
			+ "migration/bootstrap mechanism, add it to this test's Allowlist with a reason — do not add "
			+ "it to make ordinary fixture setup pass. Offenders:\n"
			+ string.Join("\n", offenders));
	}

	// The allowlist may only SHRINK — same contract as DbLayerGuardTests' two allowlists. A stale
	// entry hides work that is already done and silently re-grants the exemption to whoever next
	// edits that file for an unrelated reason.
	[Fact]
	public void AllowlistEntries_AreStillNeeded()
	{
		var matchedNames = MatchingFiles().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);

		var stale = Allowlist.Keys
			.Where(f => !matchedNames.Contains(f))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

		stale.Should().BeEmpty(
			"these files no longer call a real schema Ensure/MigrationRunner.Run directly — either they "
			+ "were converted to TestSchema.* (delete the line: the allowlist only ever shrinks, and a "
			+ "stale entry hides work that is already done) or the file was renamed/removed (delete the "
			+ "line, it now protects nothing). Stale entries:\n  " + string.Join("\n  ", stale));
	}

	// Guard-the-guard, mirroring DbLayerGuardTests.TheGuard_ActuallyInspectsSomething: if the sweep or
	// the pattern ever silently matched nothing, both tests above pass by vacuity. A separate
	// seed-a-violation-and-watch-it-get-caught test is not needed here the way it is there — every
	// Allowlist entry above IS a live, permanent match, so AllowlistEntries_AreStillNeeded already
	// fails loudly the instant the pattern (or the sweep) stops seeing any one of them. What still
	// needs pinning is that the sweep really walks the real tree and the regex isn't accidentally
	// inert or accidentally universal.
	[Fact]
	public void TheGuard_ActuallyInspectsSomething()
	{
		TestSourceFiles().Should().HaveCountGreaterThan(200, "the source scan must see the real test tree");
		TestSourceFiles().Select(Path.GetFileName).Should().Contain("TestSchema.cs");

		MatchingFiles().Should().HaveCount(Allowlist.Count,
			"every match found in the real tree must be exactly the allowlisted set — more means a new "
			+ "bypass slipped in (fails NoTestBypassesTheSchemaTemplate too), fewer means the pattern "
			+ "went blind to one of them (fails AllowlistEntries_AreStillNeeded too, but this pins the "
			+ "count directly)");

		DirectSchemaCall.IsMatch("TasksSchema.Ensure(cs);").Should().BeTrue();
		DirectSchemaCall.IsMatch("PetBox.Tasks.Data.TasksSchema.Ensure(cs);").Should().BeTrue(
			"a namespace-qualified call must not evade the scan — GoldenSchemaTests spells it exactly this way");
		DirectSchemaCall.IsMatch("MigrationRunner.Run(cs);").Should().BeTrue();
		DirectSchemaCall.IsMatch("MigrationRunner.Run(cs, typeof(M001_LogBaseline).Assembly);").Should().BeTrue();
		DirectSchemaCall.IsMatch("TestSchema.Tasks(cs);").Should().BeFalse();
		DirectSchemaCall.IsMatch("TestSchema.Core(cs);").Should().BeFalse();
		DirectSchemaCall.IsMatch("Force MigrationRunner.Run on the test DB up front").Should().BeFalse(
			"a comment merely NAMING the mechanism without the call syntax must not trip the scan");
	}
}
