using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// PRODUCTION DURABILITY GUARD — the one thing SqliteDurabilityTests structurally cannot check.
//
// SqliteDurability.Relaxed is a settable process-wide property, and it is a BLANKET override: when
// it is set it replaces every tier's chosen value at once. Production gets the durability each tier
// picked for itself (SqliteTier.Durable → FULL, SqliteTier.Telemetry → NORMAL) for exactly one
// reason — nothing under src/ ever assigns this property. That claim is load-bearing: it is the
// whole argument that relaxing fsync for the test suite did not quietly relax it for a deployed
// PetBox too, and it is the reason the per-tier decision means anything at all. A single assignment
// under src/ would collapse all eight tiers onto one value and no tier's choice would survive.
//
// SqliteDurabilityTests reads `PRAGMA synchronous` back through the production factories and
// asserts each tier's value, but to model "a deployed process" it must first set `Relaxed = null`
// ITSELF. So the day somebody adds `SqliteDurability.Relaxed = "OFF"` to a startup path in src/ —
// chasing a benchmark, say — those tests keep passing: they null the property before they look. The
// regression would reach production silently, and the only thing standing in its way today is a
// comment. This guard is what makes the claim fail out loud instead.
//
// Note what this does NOT guard, so nobody mistakes it for more than it is: the tier CONSTANTS in
// SqliteDurability (DurableSynchronous/TelemetrySynchronous) are ordinary source, and editing one
// is a deliberate, reviewable change to what PetBox promises — SqliteDurabilityTests fails loudly
// on it. This guard exists only for the back door that would silently outrank all of them.
//
// Naive by design (a text scan, same tradeoff as DbLayerGuardTests and the other guards here): it
// is a guardrail against an honest future edit, not a lexer defending against someone determined to
// evade it.
public sealed class SqliteDurabilityGuardTests
{
	// Any assignment to the property: `SqliteDurability.Relaxed =`, or a bare `Relaxed =` inside
	// SqliteDurability.cs's own body. Not `==`, which is a comparison.
	static readonly Regex Assignment = new(
		@"SqliteDurability\s*\.\s*Relaxed\s*=(?!=)",
		RegexOptions.Compiled);

	static string StripComments(string source)
	{
		var noBlock = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
		return Regex.Replace(noBlock, @"//[^\n]*", "");
	}

	static string SrcDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src");
			if (Directory.Exists(Path.Combine(candidate, "PetBox.Web"))) return candidate;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("src/ (with PetBox.Web) not found walking up from the test bin.");
	}

	static IReadOnlyList<string> ProductSourceFiles() =>
		[.. Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories)
			.Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

	[Fact]
	public void NothingUnderSrc_AssignsSqliteDurabilityRelaxed()
	{
		var offenders = ProductSourceFiles()
			.Select(p => (Path: p, Text: StripComments(File.ReadAllText(p))))
			.Where(f => Assignment.IsMatch(f.Text))
			.Select(f => Path.GetRelativePath(SrcDir(), f.Path))
			.ToList();

		offenders.Should().BeEmpty(
			"production durability rests entirely on SqliteDurability.Relaxed staying null in a deployed "
			+ "process — null means each tier gets the value it chose (Durable → FULL, an fsync per commit; "
			+ "Telemetry → NORMAL), while a set value overrides ALL of them at once. The suite relaxes it to "
			+ "OFF from tests/TestDurability.cs, which is compiled into the test assemblies alone. An "
			+ "assignment under src/ would push that blanket relaxation into the deployed product, and "
			+ "SqliteDurabilityTests CANNOT catch it: those tests set Relaxed = null themselves to model a "
			+ "deployed process, so they would stay green while production stopped fsyncing user data. If a "
			+ "deployment genuinely needs different durability, change the tier a database is assigned to, or "
			+ "make it configuration with its own test — do not assign this property. Offenders:\n  "
			+ string.Join("\n  ", offenders));
	}

	// Guard the guard: "no file matches" is vacuously true if the sweep reads nothing or the pattern
	// stopped matching. Same check the other text-scan guards in this folder carry.
	[Fact]
	public void TheSweep_ReadsTheProductTreeAndThePatternStillMatches()
	{
		ProductSourceFiles().Should().HaveCountGreaterThan(200,
			"src/ holds many hundreds of .cs files — a much smaller number means the sweep stopped "
			+ "finding the product tree, not that the product shrank");

		ProductSourceFiles().Should().Contain(p => p.EndsWith("SqliteDurability.cs", StringComparison.Ordinal),
			"the file that declares the property must be inside the scanned set, or the guard is "
			+ "watching the wrong tree");

		Assignment.IsMatch("SqliteDurability.Relaxed = \"OFF\";").Should().BeTrue(
			"the detector must match the exact form it exists to forbid");
		Assignment.IsMatch("if (SqliteDurability.Relaxed == null) return;").Should().BeFalse(
			"a comparison is not an assignment — flagging it would make the guard cry wolf on a read");
	}
}
