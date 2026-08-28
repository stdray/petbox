using System.Text.RegularExpressions;

namespace PetBox.Tests.Infra;

// deploy/backup/*.sh run inside the petbox-backup sidecar, where nothing else can check them: they
// execute under busybox ash in restic/restic:0.18.1, on a schedule, against repositories no test
// can reach. These assertions are the only thing standing between a plausible-looking edit and a
// backup system that keeps writing snapshots while quietly never pruning or verifying them again.
//
// All of it guards work/backup-deploy-kills-restic-stale-lock (2026-08-28): three stack
// re-creations in six minutes SIGKILLed restic mid-push, the abandoned repository lock blocked
// every subsequent `forget --prune` and `check` on the R2 repo for hours, and the only outward
// symptom was a Telegram alert about one failed leg — snapshots themselves kept succeeding.
// Same rationale as ComposeStopGraceTests next to this file.
public sealed class BackupSidecarScriptTests
{
	static string FindDeployFile(params string[] parts)
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
			if (File.Exists(candidate))
				return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException($"{string.Join('/', parts)} not found walking up from test bin");
	}

	// Shell line continuations would otherwise hide half of a restic invocation from a regex, and
	// "the flag is missing" and "the flag is on the next line" must not look the same to a test.
	static string[] LogicalLines(string script) =>
		Regex.Replace(script, @"\\\r?\n\s*", " ").Split('\n');

	static string[] BackupScript() =>
		LogicalLines(File.ReadAllText(FindDeployFile("deploy", "backup", "backup.sh")));

	static string EntrypointScript() =>
		File.ReadAllText(FindDeployFile("deploy", "backup", "entrypoint.sh"));

	// The one restic call of a given subcommand inside push(). Comments are excluded so that
	// merely *describing* a flag in prose can never satisfy an assertion about running it.
	static string ResticCall(string subcommand)
	{
		var matches = BackupScript()
			.Select(l => l.Trim())
			.Where(l => !l.StartsWith('#'))
			.Where(l => l.Contains("restic -r \"$repo\" " + subcommand, StringComparison.Ordinal))
			.ToArray();

		matches.Should().ContainSingle(
			"deploy/backup/backup.sh must still run exactly one `restic {0}` per leg", subcommand);
		return matches[0];
	}

	[Fact]
	public void Backup_Groups_Snapshots_By_Host_And_Tags()
	{
		ResticCall("backup").Should().Contain("--group-by host,tags",
			"the source path is /data/backups/<timestamp>-auto and so is named differently every " +
			"run, so restic's default grouping (host,paths) never matched a previous snapshot: " +
			"every run logged `no parent snapshot found, will read all files` and re-uploaded the " +
			"entire data set to R2");
	}

	[Fact]
	public void Forget_Groups_Snapshots_By_Host_And_Tags()
	{
		// The one that actually costs money and hides breakage. This is not cosmetic symmetry with
		// the backup call: with default (host,paths) grouping, forget saw every run as its own
		// group and dutifully kept a full 7-daily/4-weekly set FOR EACH, so retention never
		// removed anything — 429 snapshots had piled up in the full repo by 2026-08-28.
		ResticCall("forget").Should().Contain("--group-by host,tags",
			"otherwise each run's snapshot forms its own retention group and nothing is ever " +
			"forgotten, however correct --keep-daily/--keep-weekly look");
	}

	[Theory]
	[InlineData("forget")]
	[InlineData("check")]
	public void Operations_Needing_An_Exclusive_Lock_Wait_For_It(string subcommand)
	{
		ResticCall(subcommand).Should().Contain("--retry-lock",
			"restic 0.18.1 defaults to no retries — the incident log read `waiting up to 0s for " +
			"the lock` and the leg failed outright. With a retry window a conflicting lock is " +
			"waited out instead of failing the run");
	}

	[Fact]
	public void Stale_Locks_Are_Swept_Between_The_Backup_And_The_Exclusive_Phase()
	{
		var lines = BackupScript().Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToArray();
		var backup = Array.FindIndex(lines, l => l.Contains("restic -r \"$repo\" backup", StringComparison.Ordinal));
		var unlock = Array.FindIndex(lines, l => l.Contains("restic -r \"$repo\" unlock", StringComparison.Ordinal));
		var forget = Array.FindIndex(lines, l => l.Contains("restic -r \"$repo\" forget", StringComparison.Ordinal));

		unlock.Should().BeGreaterThan(backup,
			"the sweep belongs as late as possible, so a lock that went stale while the backup " +
			"above was running is caught too");
		unlock.Should().BeLessThan(forget,
			"its whole purpose is to clear the way for the exclusive phase (forget --prune, check)");
	}

	[Fact]
	public void The_Stale_Lock_Sweep_Never_Removes_Locks_That_Are_Still_Alive()
	{
		// Executable lines only: the header comment explains at length why --remove-all is wrong,
		// and a test that cannot tell running code from prose about it is worse than no test.
		var invocations = BackupScript()
			.Select(l => l.Trim())
			.Where(l => !l.StartsWith('#') && l.Contains("restic ", StringComparison.Ordinal))
			.ToArray();

		invocations.Should().NotContain(l => l.Contains("--remove-all", StringComparison.Ordinal),
			"`unlock --remove-all` deletes locks restic does NOT consider stale — including the " +
			"one held by a legitimately running parallel backup, which would corrupt exactly the " +
			"situation the retry window exists to survive. Plain `unlock` removes only stale locks");
	}

	[Fact]
	public void A_Terminated_Backup_Releases_The_Repository_Lock_It_Holds()
	{
		var script = File.ReadAllText(FindDeployFile("deploy", "backup", "backup.sh"));

		script.Should().MatchRegex(@"trap\s+on_term\s+TERM",
			"a deploy stopping the container mid-run is what stranded the lock in the first place");
		script.Should().Contain("restic -r \"$CURRENT_REPO\" unlock",
			"the handler has to know WHICH repo to release: push() exports the S3 credentials per " +
			"leg, so there is no other notion of a current repository");

		// The part that is easy to get wrong and impossible to notice: ash resets traps to their
		// default action inside a ( ) subshell, and every leg's restic runs in exactly such a
		// subshell (see run_leg). A trap armed only at top level is dead code where it matters.
		var push = Regex.Match(script, @"^push\(\)\s*\{.*?^\}", RegexOptions.Singleline | RegexOptions.Multiline);
		push.Success.Should().BeTrue("deploy/backup/backup.sh must still define push()");
		push.Value.Should().MatchRegex(@"trap\s+on_term\s+TERM",
			"the trap must be re-armed INSIDE push(), which is what runs in run_leg's subshell — " +
			"ash does not inherit the top-level handler there");
	}

	[Fact]
	public void Entrypoint_Does_Not_Push_A_Full_Backup_On_Every_Container_Start()
	{
		// The root cause. A full run on every start meant every deploy pushed the whole data set
		// again (~240 MiB read / ~130 MiB written to R2 apiece) and put a live restic under the
		// next deploy's kill — three deploys in six minutes, three chances to strand a lock.
		var invocations = EntrypointScript()
			.Split('\n')
			.Select(l => l.Trim())
			.Where(l => !l.StartsWith('#'))
			.Where(l => l.Contains("/usr/local/bin/backup.sh", StringComparison.Ordinal))
			.ToArray();

		invocations.Should().OnlyContain(
			l => l.Contains("crontabs", StringComparison.Ordinal) || l.Contains("pgrep", StringComparison.Ordinal),
			"backup.sh may only be referenced when writing the crontab or when waiting for a " +
			"running one during shutdown — never invoked directly at container start");
	}

	[Fact]
	public void Entrypoint_Probes_Repo_Reachability_Without_Writing_Anything()
	{
		var script = EntrypointScript();

		script.Should().Contain("cat config",
			"the point of the start-up run was to catch bad credentials on a fresh deploy instead " +
			"of waiting up to 6 h for the first cron tick; `restic cat config` keeps that signal " +
			"by fetching and decrypting one small object — no lock, no bytes written");
		script.Should().Contain("timeout \"$PROBE_TIMEOUT_SECONDS\"",
			"measured: against an unreachable or not-yet-created bucket `cat config` sat on " +
			"restic's backend backoff for over ten minutes. Unbounded, a diagnostic probe would " +
			"delay the very schedule it is diagnosing");
	}

	[Fact]
	public void Entrypoint_Stays_Pid1_So_Sigterm_Reaches_Restic()
	{
		var script = EntrypointScript();

		// Measured on restic/restic:0.18.1: with crond exec'd as PID 1, `docker stop` waits out the
		// entire grace period and ends in SIGKILL (exit 137) whether or not a job is running — a
		// PID-1 process only receives signals it has installed a handler for, and crond installs
		// none for TERM, nor does it forward anything to the job it spawned. So restic never saw a
		// shutdown signal at all. Raising stop_grace_period without this is pure deploy latency.
		script.Should().NotMatchRegex(@"(?m)^\s*exec\s+crond",
			"exec'ing crond makes it PID 1, where SIGTERM is silently discarded and never reaches " +
			"the running restic — the grace period then only postpones the same SIGKILL");
		script.Should().MatchRegex(@"(?m)^\s*crond\b.*&\s*$",
			"crond must run as a child so this shell stays PID 1 and can relay the signal");
		script.Should().MatchRegex(@"trap\s+term_handler\s+TERM",
			"and PID 1 must actually install a TERM handler, or the kernel discards the signal");
	}
}
