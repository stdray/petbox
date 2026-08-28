namespace PetBox.Tests.Infra;

// 28.08.2026: two `deploy` jobs from different CI runs (different commits, so the workflow-level
// `concurrency:` group — keyed by sha — didn't dedupe them) ran `docker compose up` on the prod
// host at the same time. One clobbered the other mid-deploy and failed its health check; the
// other's backup sidecar got its restic lock cut out from under it. See
// work/ci-concurrent-deploys-not-serialized. Nothing but a test keeps this fixed once someone next
// edits the workflow — the failure mode is invisible until two deploys happen to overlap.
public sealed class CiDeployConcurrencyTests
{
	static string FindCiYaml()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, ".github", "workflows", "ci.yml");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException(".github/workflows/ci.yml not found walking up from test bin");
	}

	// The `deploy:` job block only — the workflow-level `concurrency:` block (which groups by
	// commit sha on purpose, so a `main` push and the `deploy` tag move on the same sha don't
	// double-run CI) lives outside this block and must NOT be what these assertions match against.
	static string DeployJobBlock()
	{
		var lines = File.ReadAllLines(FindCiYaml());
		var start = Array.FindIndex(lines, l => l.StartsWith("  deploy:", StringComparison.Ordinal));
		start.Should().BeGreaterThanOrEqualTo(0, "ci.yml must still declare a `deploy` job");

		// Next line back at the same (2-space) job-key indent ends the block.
		var end = Array.FindIndex(lines, start + 1, l =>
			l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l.TrimEnd().EndsWith(':'));
		if (end < 0) end = lines.Length;

		return string.Join('\n', lines[start..end]);
	}

	[Fact]
	public void Deploy_Job_Has_Its_Own_Concurrency_Group()
	{
		DeployJobBlock().Should().MatchRegex(@"(?m)^\s{4}concurrency:\s*$",
			"the deploy job needs a job-level concurrency block, separate from the sha-keyed " +
			"workflow-level one, so two different commits' deploy jobs still serialize");
	}

	[Fact]
	public void Deploy_Concurrency_Group_Is_Fixed_Not_Keyed_By_Commit()
	{
		var block = DeployJobBlock();
		var match = System.Text.RegularExpressions.Regex.Match(block, @"(?m)^\s{4}concurrency:\s*\n\s{6}group:\s*(\S+)");
		match.Success.Should().BeTrue("expected a `group:` line directly under the job-level `concurrency:`");

		var group = match.Groups[1].Value;
		group.Should().NotContain("sha", "a sha-keyed group is exactly the workflow-level bug: " +
			"two different commits would land in different groups and never serialize against " +
			"each other, which is what let two deploys race on 28.08.2026");
		group.Should().NotContain("${{", "the group must be a fixed literal (e.g. deploy-prod) — " +
			"an expression risks re-introducing a per-commit key");
	}

	[Fact]
	public void Deploy_Job_Does_Not_Cancel_An_In_Progress_Deploy()
	{
		DeployJobBlock().Should().MatchRegex(@"(?m)^\s{6}cancel-in-progress:\s*false\s*$",
			"cancel-in-progress: true would abort a running `docker compose up` mid-way and leave " +
			"prod in an unknown state — exactly the failure mode this card fixes. GitHub Actions " +
			"queues only the newest pending run and lets the running one finish, which is the " +
			"desired 'current deploy finishes, then the latest commit deploys next' behaviour.");
	}
}
