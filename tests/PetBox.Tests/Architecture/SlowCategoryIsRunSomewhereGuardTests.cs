namespace PetBox.Tests.Architecture;

// `Category=Slow` is skipped by the default Cake `Test` run and included only when the caller
// passes `--slowTests=true`. Exactly one caller does: the CI workflow. That makes the arrangement
// "runs in CI, not on your laptop" — but it is one deleted flag away from "runs nowhere", and the
// failure is SILENT: the suite goes green faster, and the only signal that a production-race guard
// stopped executing is its absence from a log nobody reads.
//
// So the flag is pinned here. If the exclusion in build.cs exists, CI must still opt in.
public sealed class SlowCategoryIsRunSomewhereGuardTests
{
	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "PetBox.Web"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("repo root (with src/PetBox.Web) not found walking up from the test bin.");
	}

	[Fact]
	public void CiOptsIntoTheSlowCategory_SinceTheDefaultRunSkipsIt()
	{
		var buildCs = File.ReadAllText(Path.Combine(RepoRoot(), "build.cs"));
		var ci = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

		// Only meaningful while the default run actually excludes the category. If someone removes
		// the exclusion, Slow runs everywhere and this guard has nothing left to protect.
		if (!buildCs.Contains("Category!=Slow", StringComparison.Ordinal)) return;

		// The COMMAND line, not the file. A first cut asserted on the whole ci.yml and passed while
		// the flag was deleted from the `run:` step, because the comment above it still spelled the
		// flag — a guard that reads prose instead of the thing that executes.
		var runLine = ci.Split('\n')
			.Select(l => l.Trim())
			.FirstOrDefault(l => l.StartsWith("run:", StringComparison.Ordinal) && l.Contains("--target=Test", StringComparison.Ordinal));

		runLine.Should().NotBeNull("the workflow must still have a `run:` step invoking the Cake Test target");

		runLine.Should().Contain("--slowTests=true",
			"build.cs excludes Category=Slow from the default Test run, so CI is the ONLY place those "
			+ "tests execute — and it only runs them because the workflow passes --slowTests=true. "
			+ "Dropping that flag does not fail anything: the suite just goes green a little faster "
			+ "while CrossScopeSearchFanOutIntegrationTests, which guards a race that reached "
			+ "production, silently stops running anywhere. Either keep the flag, or delete the "
			+ "Category!=Slow exclusion in build.cs so the tests run everywhere again");
	}

	[Fact]
	public void TheGuard_ReadsTheRealFiles()
	{
		var root = RepoRoot();
		File.Exists(Path.Combine(root, "build.cs")).Should().BeTrue(
			"the guard asserts on build.cs's content — if the path stopped resolving, the check above "
			+ "would pass by reading nothing");
		File.Exists(Path.Combine(root, ".github", "workflows", "ci.yml")).Should().BeTrue(
			"same for the workflow file: a missing path must fail here, not quietly satisfy the guard");
	}
}
