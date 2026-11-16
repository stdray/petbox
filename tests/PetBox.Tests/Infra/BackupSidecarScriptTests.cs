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

	// work/backup-full-leg-firstvds-s3-domain-moved: `snapshots || init` turned ANY open failure
	// (DNS, auth, a moved endpoint) into `restic init`, so an endpoint pointing at a live S3 with no
	// repo would have created an EMPTY repo, gone green and masked the lost history.
	[Fact]
	public void Init_Is_Guarded_By_Exit_Code_10_And_An_Explicit_Opt_In()
	{
		var code = BackupScript().Select(l => l.Trim()).Where(l => !l.StartsWith('#')).ToArray();

		code.Should().NotContain(l => Regex.IsMatch(l, @"snapshots[^|]*\|\|.*init"),
			"`snapshots || init` is the exact shape that initialised an empty repo on any open failure");

		var initCalls = code.Where(l => Regex.IsMatch(l, @"restic\s+-r\s+""\$repo""\s+init")).ToArray();
		initCalls.Should().ContainSingle("push() runs `restic init` at exactly one, guarded place");

		var joined = string.Join('\n', code);
		joined.Should().Contain("-eq 10",
			"restic exit code 10 (>= 0.17) is the only unambiguous 'repository does not exist' signal");
		joined.Should().Contain("RESTIC_ALLOW_INIT",
			"even a truly missing repo is only initialised on explicit opt-in, so a changed endpoint " +
			"or wiped repo fails loudly instead of silently starting an empty history");
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

	// The body of a named shell function, so an assertion about WHERE something happens cannot be
	// satisfied by the same text sitting somewhere else in the file.
	static string FunctionBody(string name)
	{
		var script = File.ReadAllText(FindDeployFile("deploy", "backup", "backup.sh"));
		var m = Regex.Match(script, @"^" + name + @"\(\)\s*\{.*?^\}", RegexOptions.Singleline | RegexOptions.Multiline);
		m.Success.Should().BeTrue($"deploy/backup/backup.sh must still define {name}()");
		return m.Value;
	}

	// ── work/backup-leg-timeout-and-probe-alert ──────────────────────────────────────────────
	// Three failures these guard, all of them silent-by-nature:
	//   1. a wedged S3 endpoint keeping a run alive past the next cron tick (restic has no
	//      overall deadline and its backend backoff was measured at >10 min on one call);
	//   2. a start-up probe that finds broken credentials and only writes a log line;
	//   3. a shutdown whose diagnosis never reached `docker logs`.

	[Fact]
	public void No_Restic_Call_Inside_A_Leg_Can_Run_Without_A_Time_Limit()
	{
		// A per-call timeout would not do: the budget belongs to the LEG, so backup + unlock +
		// forget + check together cannot outlast it however the time falls between them. Requiring
		// the prefix to be exactly `timeout "$(leg_remaining)" ` also pins the signal to the
		// default (TERM) — `timeout -s KILL` would strand the very lock the trap exists to release.
		var calls = 0;
		foreach (var line in FunctionBody("push").Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#')))
		{
			foreach (Match m in Regex.Matches(line, @"restic\s+-r\b"))
			{
				calls++;
				line[..m.Index].Should().EndWith("timeout \"$(leg_remaining)\" ",
					"an unbounded restic call re-opens the exact hole this budget closes: one " +
					"unreachable endpoint stalls the leg indefinitely and runs start stacking");
			}
		}

		calls.Should().BeGreaterThanOrEqualTo(5,
			"push() still has to run snapshots/init, backup, unlock, forget and check — if this " +
			"count dropped, the loop above may simply have stopped finding the calls");
	}

	[Fact]
	public void An_Exhausted_Leg_Budget_Never_Degrades_Into_No_Limit()
	{
		// The trap in the whole design: `timeout 0` means NO time limit in busybox AND coreutils.
		// Without the floor, a leg that had already spent its budget would hand 0 (or a negative
		// number) to the next call and run forever — the failure mode inverted.
		FunctionBody("leg_remaining").Should().MatchRegex(@"\[ ""\$_left"" -gt 0 \] \|\| _left=1",
			"a spent budget must round UP to a 1 s timeout, never down to `timeout 0`");
	}

	[Fact]
	public void Each_Leg_Gets_Its_Own_Deadline_Armed_Before_It_Starts()
	{
		var runLeg = FunctionBody("run_leg");

		runLeg.Should().Contain("LEG_DEADLINE=$(( $(date +%s) + LEG_TIMEOUT_SECONDS ))",
			"the deadline is what leg_remaining counts down from; left at 0 every restic call " +
			"would get the 1 s floor and no leg could ever succeed");
		runLeg.IndexOf("LEG_DEADLINE=", StringComparison.Ordinal).Should().BeLessThan(
			runLeg.IndexOf("run_leg_path", StringComparison.Ordinal),
			"and it has to be armed BEFORE the subshell that inherits it (and before the egress " +
			"preflight inside it, which is bounded by its own curl timeouts but still runs inside " +
			"the leg)");
	}

	[Fact]
	public void The_Leg_Budget_Outlasts_The_Lock_Retries_And_Two_Legs_Still_Fit_The_Cron_Interval()
	{
		// The number is only defensible relative to two others that live elsewhere, so assert the
		// relationship rather than the literal — same reasoning as ComposeStopGraceTests.
		var backup = File.ReadAllText(FindDeployFile("deploy", "backup", "backup.sh"));

		var legMatch = Regex.Match(backup, @"LEG_TIMEOUT_SECONDS=""\$\{BACKUP_LEG_TIMEOUT_SECONDS:-(\d+)\}""");
		legMatch.Success.Should().BeTrue("backup.sh must define a default leg time budget");
		var lockMatch = Regex.Match(backup, @"RETRY_LOCK=""\$\{RESTIC_RETRY_LOCK:-(\d+)m\}""");
		lockMatch.Success.Should().BeTrue("backup.sh must still define a lock retry window in minutes");
		var cronMatch = Regex.Match(EntrypointScript(), @"BACKUP_CRON:-\S+ \*/(\d+) ");
		cronMatch.Success.Should().BeTrue("entrypoint.sh must still default to an every-N-hours cron");

		var leg = int.Parse(legMatch.Groups[1].Value);
		var lockRetrySeconds = int.Parse(lockMatch.Groups[1].Value) * 60;
		var cronSeconds = int.Parse(cronMatch.Groups[1].Value) * 3600;

		leg.Should().BeGreaterThan(3 * lockRetrySeconds,
			"backup, forget and check may EACH wait out a conflicting lock for the full retry " +
			"window. A budget shorter than all three would silently cancel the retry window that " +
			"exists to survive a stale lock — the run would fail on contention instead of waiting");
		(2 * leg).Should().BeLessThanOrEqualTo(cronSeconds / 2,
			"the two legs run in sequence, and a run must finish with the whole interval to spare " +
			"rather than still be going when the next tick fires and starts stacking runs");
	}

	[Fact]
	public void A_Shutdown_During_A_Leg_Still_Reaches_Docker_Logs()
	{
		// run_leg captures the leg into a file and only `cat`s it once the subshell returns. On
		// SIGTERM the parent's trap runs INSTEAD of that `cat` and exits, so the leg's own
		// "SIGTERM during a leg on <repo>" line — written inside the subshell — never appeared in
		// `docker logs`: the unlock happened, but a shutdown could not be diagnosed. Verified on
		// restic/restic:0.18.1 against both the old and the new script.
		FunctionBody("on_term").Should().Contain("cat \"$CURRENT_LEG_LOG\"",
			"the handler has to replay the captured leg before it exits, or the only trace of what " +
			"happened dies with the process");
		FunctionBody("run_leg").Should().Contain("CURRENT_LEG_LOG=\"$_log\"",
			"and the handler can only replay a log it has been told the name of");
		FunctionBody("push").Should().Contain("CURRENT_LEG_LOG=\"\"",
			"but NOT in push()'s re-armed copy of the trap, which runs in the subshell whose stdout " +
			"already IS that file — replaying it there appends the file to itself");
	}

	[Fact]
	public void A_Failed_Reachability_Probe_Pages_Instead_Of_Only_Logging()
	{
		// The probe existed to catch broken credentials on a fresh deploy, but it only printed a
		// WARNING while the Telegram alert lived in backup.sh — so nobody learned about it until a
		// cron tick failed, up to 6 h later.
		var script = EntrypointScript();

		script.Should().Contain("api.telegram.org",
			"a start-up probe that finds a dead repo has to page someone, not write a log line");
		script.Should().Contain("$STATE_DIR/alert-status",
			"and it must share backup.sh's alert state rather than open a second channel: under " +
			"`restart: unless-stopped` a crash loop would otherwise page once per container start");
		script.Should().Contain("ALERT_REPEAT_HOURS",
			"same repeat window as a failed run, for the same reason");
	}

	[Fact]
	public void One_Unreachable_Repo_Is_Already_Enough_To_Page()
	{
		// Owner decision 2026-08-28, taken with the deploy-noise trade-off in view: alerting only
		// when BOTH repos are unreachable is quieter but misses a single dead backend — which is
		// one of the two independent offsite copies gone.
		EntrypointScript().Should().MatchRegex(@"\[ -z ""\$PROBE_FAILED"" \] \|\| alert_probe_failure",
			"the alert fires on a non-empty list of failed probes, not on a both-failed condition");
	}

	[Fact]
	public void The_Probe_Alert_Only_Ever_Writes_Failure_Into_The_Shared_Alert_State()
	{
		// The subtle one. backup.sh sends exactly one "recovered" message, when it sees a previous
		// `fail` in the state file. If the probe wrote `ok` on success it would consume that
		// transition, and a genuinely broken backup would recover silently.
		var script = EntrypointScript();

		Regex.Matches(script, @">\s*""\$_state_file""").Should().HaveCount(1,
			"exactly one write to the shared alert state, on the failure path");
		script.Should().Contain(@"printf 'fail %s\n' ""$_now"" > ""$_state_file""",
			"and that write says `fail`: marking the state `ok` here would swallow backup.sh's " +
			"one-shot recovery message and re-arm the alert for a repo that is still broken");
	}

	// ── work/backup-leg-direct-first-proxy-fallback (2026-11-16) ────────────────────────────
	// A repository can be perfectly healthy and still be unreachable FROM THIS HOST: from apps1,
	// TCP to firsts3.ru timed out on 5 attempts out of 5 while ICMP answered and R2 answered
	// directly — the network in front of 37.46.135.0/24 drops TCP from this prefix. restic reports
	// that as "unreachable endpoint, bad credentials, wrong RESTIC_PASSWORD or TLS" and spends
	// minutes of backoff deciding which, so each leg now picks its own egress path first
	// (deploy/backup/egress.sh). These assertions pin the three properties the fix is worth nothing
	// without: the preflight is curl and never restic, DIRECT is tried before the proxy, and the
	// proxy is never exported beyond the one leg that chose it.

	static string EgressScript() =>
		File.ReadAllText(FindDeployFile("deploy", "backup", "egress.sh"));

	// Executable lines only: the header comments deliberately NAME restic, firsts3.ru and the
	// incident's timings while explaining why none of them may appear in the code.
	static string ExecutableLines(string script) =>
		string.Join('\n', script.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith('#')));

	static string FunctionBodyIn(string script, string name)
	{
		var m = Regex.Match(script, @"^" + name + @"\(\)\s*\{.*?^\}", RegexOptions.Singleline | RegexOptions.Multiline);
		m.Success.Should().BeTrue($"{name}() must still be defined");
		return m.Value;
	}

	[Fact]
	public void The_Egress_Preflight_Is_A_Curl_Probe_And_Never_A_Restic_Attempt()
	{
		var code = ExecutableLines(EgressScript());

		code.Should().Contain("curl",
			"path selection has to answer in seconds; asking restic is what cost minutes — `restic " +
			"cat config` against an unreachable bucket was measured at over ten minutes of backend " +
			"backoff on this image");
		code.Should().NotContain("restic",
			"a preflight that can invoke restic re-opens exactly the hole it exists to close");
		code.Should().Contain("--noproxy '*'",
			"the DIRECT probe must stay direct even when an ambient HTTPS_PROXY is set — otherwise a " +
			"live proxy makes every path look direct and the fallback never triggers");
		code.Should().Contain("--connect-timeout").And.Contain("--max-time",
			"an unbounded probe would be as bad as the restic call it replaced");
	}

	[Fact]
	public void No_Endpoint_Host_Is_Baked_Into_The_Path_Selection()
	{
		foreach (var file in new[] { "egress.sh", "backup.sh", "entrypoint.sh" })
		{
			var code = ExecutableLines(File.ReadAllText(FindDeployFile("deploy", "backup", file)));
			code.Should().NotContain("firsts3.ru",
				"{0} must take the host from FVDS_S3_ENDPOINT / R2_S3_ENDPOINT — a baked-in hostname " +
				"silently keeps probing an endpoint the leg no longer pushes to", file);
			code.Should().NotContain("cloudflarestorage.com", "{0} must derive the host too", file);
		}
	}

	[Fact]
	public void Each_Leg_Probes_The_Direct_Path_Before_It_Considers_The_Proxy()
	{
		var leg = FunctionBody("run_leg_path");

		leg.IndexOf("egress_direct_ok", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0,
			"the leg has to probe before it decides anything");
		leg.IndexOf("egress_direct_ok", StringComparison.Ordinal).Should().BeLessThan(
			leg.IndexOf("egress_proxy_ok", StringComparison.Ordinal),
			"DIRECT is the priority and the choice is per leg: the first fix for this incident forced " +
			"a container-wide HTTPS_PROXY, which put the perfectly reachable R2 leg behind a proxy on " +
			"another host — an extra hop and an extra dependency for a leg that needs neither");
	}

	[Fact]
	public void The_Proxy_Is_Exported_Only_Inside_One_Legs_Subshell()
	{
		var backup = File.ReadAllText(FindDeployFile("deploy", "backup", "backup.sh"));
		var pushOnce = FunctionBody("push_once");

		Regex.Matches(ExecutableLines(backup), @"egress_env\s").Should().HaveCount(1,
			"one call site, inside push_once's subshell — an egress_env call at any other level " +
			"would leak the proxy onto the other leg, onto the alert/heartbeat curls and onto the " +
			"probes themselves");
		pushOnce.Should().Contain("egress_env");
		pushOnce.IndexOf("set -e", StringComparison.Ordinal).Should().BeLessThan(
			pushOnce.IndexOf("push \"$@\"", StringComparison.Ordinal),
			"errexit is armed here and only here: the first failing restic call must abort the " +
			"ATTEMPT, while run_leg_path has to survive that failure to be able to retry");
		ExecutableLines(backup).Should().NotContain("export HTTPS_PROXY",
			"nothing in backup.sh may export the proxy globally — that is the shape of the incident's " +
			"first, over-broad fix");

		var egressEnv = FunctionBodyIn(EgressScript(), "egress_env");
		egressEnv.Should().Contain("unset HTTPS_PROXY",
			"the DIRECT path must actively clear a proxy it may have inherited, or \"direct\" is a " +
			"label on a request that still went through the proxy");
		egressEnv.Should().Contain("export HTTPS_PROXY");
	}

	[Fact]
	public void A_Failed_Path_Gets_Exactly_One_Retry_Via_The_Other_One()
	{
		var leg = FunctionBody("run_leg_path");

		Regex.Matches(leg, @"push_once\s").Should().HaveCount(2,
			"one attempt plus exactly ONE fallback: a loop here would spend the rest of the leg's " +
			"budget alternating between two paths");
		leg.Should().Contain("egress_available",
			"the fallback is only worth trying when the other path actually answers right now — " +
			"retrying into a known-dead path just burns the budget on restic's backoff");
		leg.Should().Contain("paths tried:",
			"a leg that failed has to say WHICH paths it tried, or the next reader cannot tell an " +
			"endpoint outage from a credentials problem");
	}

	[Fact]
	public void The_Retry_Shares_The_Legs_Deadline()
	{
		FunctionBody("run_leg_path").Should().NotContain("LEG_DEADLINE",
			"the deadline is armed once per leg in run_leg and every restic call counts down from it; " +
			"re-arming it for the fallback would double the leg's ceiling — LEG_TIMEOUT_SECONDS is a " +
			"ceiling on a WHOLE leg, asserted against the cron interval by another test");
	}

	[Fact]
	public void An_Unreachable_Endpoint_Fails_The_Leg_Without_Asking_Restic()
	{
		var leg = FunctionBody("run_leg_path");

		leg.Should().Contain("no usable path to",
			"a leg with no path at all must fail loudly, naming the paths it tried");
		leg.IndexOf("push_once", StringComparison.Ordinal).Should().BeGreaterThan(
			leg.IndexOf("egress_proxy_ok", StringComparison.Ordinal),
			"BOTH probes have to run before the first restic attempt — otherwise an unreachable " +
			"endpoint still costs restic's multi-minute backoff, which is the entire behaviour this " +
			"change removes");
	}

	[Fact]
	public void The_Image_Ships_The_Shared_Egress_Library()
	{
		var dockerfile = File.ReadAllText(FindDeployFile("deploy", "backup", "Dockerfile"));

		foreach (var script in new[] { "backup.sh", "entrypoint.sh" })
		{
			File.ReadAllText(FindDeployFile("deploy", "backup", script))
				.Should().Contain("/egress.sh",
					"{0} sources the shared path selection from beside itself", script);
		}

		dockerfile.Should().Contain("COPY egress.sh",
			"both scripts source egress.sh; a script copied without the library would boot the sidecar " +
			"straight into `.: egress.sh: not found` — and no local check builds this image");
	}

	[Fact]
	public void Compose_Offers_The_Proxy_As_A_Fallback_And_Never_Forces_It()
	{
		var lines = File.ReadAllLines(FindDeployFile("deploy", "compose.yaml"));
		var start = Array.FindIndex(lines, l => l.TrimEnd() == "  petbox-backup:");
		start.Should().BeGreaterThanOrEqualTo(0, "deploy/compose.yaml must still declare petbox-backup");
		var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("volumes:", StringComparison.Ordinal));
		if (end < 0) end = lines.Length;
		var block = string.Join('\n', lines[start..end]);

		block.Should().Contain("BACKUP_HTTPS_PROXY:",
			"the sidecar has to RECEIVE the proxy as an available fallback");
		block.Should().NotMatchRegex(@"(?m)^\s*HTTPS_PROXY:",
			"a container-wide HTTPS_PROXY applies to BOTH legs — it routed the reachable R2 leg " +
			"through the proxy too. The per-leg choice belongs to egress.sh, which exports the proxy " +
			"for one leg at a time");
	}

	[Fact]
	public void The_Deploy_Passes_The_Proxy_Secret_Into_The_Compose_Env()
	{
		var ci = File.ReadAllText(FindDeployFile(".github", "workflows", "ci.yml"));

		ci.Should().Contain("BACKUP_HTTPS_PROXY: ${{ secrets.BACKUP_HTTPS_PROXY }}");
		ci.Should().MatchRegex(@"envs: [^\n]*\bBACKUP_HTTPS_PROXY\b",
			"appleboy/ssh-action forwards only the names listed in `envs:` — a var present in `env:` " +
			"alone reaches the remote shell EMPTY, so the fallback would be silently absent and only " +
			"the full leg would fail, six hours later");
	}

	[Fact]
	public void The_StartUp_Probe_Asks_The_Preflight_Before_It_Asks_Restic()
	{
		var check = FunctionBodyIn(EntrypointScript(), "check_repo");

		check.IndexOf("egress_direct_ok", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
		check.IndexOf("egress_direct_ok", StringComparison.Ordinal).Should().BeLessThan(
			check.IndexOf("cat config", StringComparison.Ordinal),
			"the restic call may only happen once a path is known to answer — otherwise a deploy with " +
			"a dead endpoint puts the start-up probe back on restic's multi-minute backoff");
		check.Should().Contain("egress_env",
			"and that restic call has to GO THROUGH the chosen path, or the probe would report a repo " +
			"as unreadable while the scheduled run reads it fine via the proxy");
	}
}
