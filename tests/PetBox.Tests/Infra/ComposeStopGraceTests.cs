using PetBox.Web;

namespace PetBox.Tests.Infra;

// The two halves of the stop budget live in different systems and neither can read the other, so
// nothing but a test can keep them consistent. The failure this guards is silent by construction:
// docker SIGKILLs at its own deadline and the app never gets to say it was cut off — the only
// symptom is telemetry that stops arriving (buffered log rows, usage counters, ApiKey.LastUsedAt
// marks) after a deploy.
public sealed class ComposeStopGraceTests
{
	static string FindComposeYaml()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "deploy", "compose.yaml");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("deploy/compose.yaml not found walking up from test bin");
	}

	static string FindBackupEntrypoint()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "deploy", "backup", "entrypoint.sh");
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("deploy/backup/entrypoint.sh not found walking up from test bin");
	}

	// One service block only — petbox and petbox-backup have separate stop behaviours, and a
	// setting found anywhere else in the file would be a false pass.
	static string ServiceBlock(string service)
	{
		var lines = File.ReadAllLines(FindComposeYaml());
		var start = Array.FindIndex(lines, l => l.StartsWith("  " + service + ":", StringComparison.Ordinal));
		start.Should().BeGreaterThanOrEqualTo(0, "deploy/compose.yaml must still declare a `{0}` service", service);

		var end = Array.FindIndex(lines, start + 1, l =>
			l.Length > 2 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l.TrimEnd().EndsWith(':'));
		if (end < 0) end = lines.Length;

		return string.Join('\n', lines[start..end]);
	}

	static string PetBoxServiceBlock() => ServiceBlock("petbox");

	[Fact]
	public void PetBox_Service_Declares_A_Stop_Grace_Period()
	{
		PetBoxServiceBlock().Should().MatchRegex(@"stop_grace_period:\s*\d+s",
			"without it docker falls back to 10 s and kills the app mid-drain — the app's own " +
			"ShutdownTimeout is 30 s and cannot extend docker's patience");
	}

	[Fact]
	public void Stop_Grace_Period_Covers_The_Hosts_Whole_Budget()
	{
		var match = System.Text.RegularExpressions.Regex.Match(
			PetBoxServiceBlock(), @"stop_grace_period:\s*(\d+)s");
		match.Success.Should().BeTrue("the previous test explains this");

		var declared = TimeSpan.FromSeconds(int.Parse(match.Groups[1].Value));
		declared.Should().BeGreaterThanOrEqualTo(ShutdownBudget.MinimumStopGracePeriod,
			"docker's deadline has to outlast ShutdownTimeout ({0}) PLUS the container-disposal tail " +
			"({1}), which runs after every StopAsync has returned and is not covered by the host's " +
			"own timeout — raise this value whenever either of those grows",
			ShutdownBudget.HostShutdownTimeout, ShutdownBudget.DisposalTail);
	}

	// ── the backup sidecar ────────────────────────────────────────────────────────────────────
	// Same class of failure as the app's, different victim. restic holds a lock on the offsite
	// repository while it works; killed mid-flight it leaves that lock behind, and every later run
	// then fails retention/prune/check until the lock ages out. Three deploys inside six minutes
	// did exactly that to the R2 repo on 2026-08-28 (work/backup-deploy-kills-restic-stale-lock):
	// snapshots kept being written, so nothing looked broken from outside, while `forget --prune`
	// and `check` had silently stopped running for hours.

	[Fact]
	public void Backup_Sidecar_Declares_A_Stop_Grace_Period()
	{
		ServiceBlock("petbox-backup").Should().MatchRegex(@"stop_grace_period:\s*\d+s",
			"without it docker falls back to 10 s and SIGKILLs restic mid-push, stranding the " +
			"repository lock — the 2026-08-28 incident. restic needs time to notice the signal, " +
			"finish the object in flight and delete its lock over S3");
	}

	[Fact]
	public void Backup_Sidecar_Grace_Period_Outlasts_The_Entrypoints_Own_Shutdown_Wait()
	{
		// Two halves in two files that cannot read each other — the same situation the `petbox`
		// tests above exist for. entrypoint.sh waits for a running backup to unwind before it
		// exits; if docker's deadline were the shorter of the two, that wait would be cut off by a
		// SIGKILL and the lock would survive anyway, silently.
		var grace = System.Text.RegularExpressions.Regex.Match(
			ServiceBlock("petbox-backup"), @"stop_grace_period:\s*(\d+)s");
		grace.Success.Should().BeTrue("the previous test explains this");

		var wait = System.Text.RegularExpressions.Regex.Match(
			File.ReadAllText(FindBackupEntrypoint()), @"SHUTDOWN_WAIT_SECONDS=(\d+)");
		wait.Success.Should().BeTrue(
			"deploy/backup/entrypoint.sh must still bound how long it waits for a running backup");

		var graceSeconds = int.Parse(grace.Groups[1].Value);
		var waitSeconds = int.Parse(wait.Groups[1].Value);

		graceSeconds.Should().BeGreaterThan(waitSeconds,
			"docker must never be the one that cuts the shutdown short: entrypoint.sh waits up to " +
			"{0}s for restic to release its lock, so compose has to allow strictly more than that",
			waitSeconds);
	}
}
