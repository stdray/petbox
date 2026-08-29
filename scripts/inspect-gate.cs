// inspect-gate — the gate `jb inspectcode` does not have on its own: a pass/fail threshold.
// inspectcode has no --fail-on-issues; this script runs it, parses the SARIF report, and exits
// non-zero if anything survives at the given severity. See the doctrine comment below for how a
// confirmed false positive gets out of the survivor set — it is never done in THIS file.
//
// This gate runs in CI (.github/workflows/inspect.yml -> this script, on every branch push),
// not locally: there is no pre-push hook invoking it any more (removed along with this comment
// being wrong — see AGENTS.md for the current contract: the orchestrator waits for a green
// `inspect` CI run before merging, push itself is never blocked locally). Still runnable by hand
// for debugging a finding or checking `CleanupCode`'s effect before committing. Cost: ~45-160s
// wall-clock locally (measured on this repo; depends on warm/cold JetBrains caches); CI runs
// somewhat slower — see the `inspect` workflow's run history for current numbers.
//
// CONCURRENCY: the thing this gate has to survive is NOT "something else is building in this
// checkout" -- it is that MSBuild's worker-node pool is MACHINE-GLOBAL. `jb inspectcode` shells
// out to `MSBuild.exe ... /t:Restore /m:30` with node reuse left at its default (on), and MSBuild
// then ADOPTS idle reusable worker nodes that some OTHER process on this machine left behind --
// including ones spawned from a completely different worktree. Measured 2026-08-28: a
// `MSBuild -t:Restore -m:16` run in worktree B left 15 idle `/nodemode:1 /nodeReuse:true` nodes,
// and a gate run in worktree A then adopted and used all 15 of them (+0.27s..+1.20s CPU each).
// Those adopted nodes live in the OTHER agent's process tree, so when that agent is killed (the
// watchdog does this routinely) they die mid-build and this gate reports MSB4166 / `exited 4` --
// a false red caused by a checkout it never touched. Two mechanisms below defend against that:
// MSBUILDDISABLENODEREUSE on the jb child, and a machine-global mutex.
//
// Run manually: dotnet run scripts/inspect-gate.cs   (from the repo root; no activation step —
// there is nothing to `git config` any more, this is just a script you can invoke directly).
//
// Usage:
//   dotnet run scripts/inspect-gate.cs                                       # full run, ERROR severity
//   dotnet run scripts/inspect-gate.cs -- --severity=WARNING                 # widen the gate
//   dotnet run scripts/inspect-gate.cs -- --solution Other.slnx
//   dotnet run scripts/inspect-gate.cs -- --report path/to/existing.sarif    # skip the jb run, just re-judge a report
//   dotnet run scripts/inspect-gate.cs -- --lock-timeout=45                  # minutes to wait for the gate lock (default 30)
//   dotnet run scripts/inspect-gate.cs -- --no-lock                          # run without the machine-global lock
//   dotnet run scripts/inspect-gate.cs -- --caches-home path/to/dir          # override the computed caches-home (e.g. a stable, cacheable CI path)
//
// Exit 0: nothing survived at the given severity.
// Exit 1: at least one finding SURVIVED (printed as `file:line  ruleId  message`) — a verdict about
//         the code. Also a usage error (unknown argument).
// Exit 2: COULD NOT VERIFY — no verdict was produced at all: `jb` missing or the wrong version, or
//         inspectcode failed to run (retried, see below), or the --report file is missing. This
//         still blocks the push (fail-closed), but it is a statement about the TOOL, not the code.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// ---- suppression doctrine: THREE mechanisms, all visible in Rider, none of them THIS file ----
// A ReSharper/CLT finding that is a confirmed false positive gets out of the survivor set one of
// three ways. Reaching for the wrong one is how a stale-cache bug or a silent gate-weakening
// happens — pick by SCOPE, not by whichever file you happen to have open:
//
//   1. `severity` in `PetBox.slnx.DotSettings` (InspectionSeverities) — rule x WHOLE SOLUTION. The
//      rule itself is judged wrong for this codebase, for inspectcode AND Rider alike (see the
//      CS8602 entry there for a worked example).
//   2. `resharper_<inspection_id>_highlighting = none` in a glob-scoped section of the ROOT
//      `.editorconfig` (repo root, NOT a nested tests/.editorconfig or a per-project one) — rule x
//      SUBTREE. For when the analyzer is structurally BLIND across a whole zone (a reflection-only
//      call path, a DTO the analyzer can't see a remote consumer of) — not "noisy", blind: no
//      rewrite would satisfy it. Worked examples: the MCP-contract and tests/** sections in this
//      same .editorconfig file. An `ExternalAnnotations/**/*.xml` file (solution-adjacent,
//      auto-discovered by `jb inspectcode`) is the same tier at a finer grain — rule-family x
//      THIRD-PARTY TYPE — for when a glob would ALSO catch genuinely dead code colocated in the
//      same file/subtree (worked example: ModelContextProtocol.Core.xml marks every third-party
//      [McpServerTool]-attributed member implicitly-used without also blinding
//      ModuleExtensions.cs, which sits in the same tier and holds real dead code — confirmed and
//      unrelated worked example: ModuleMcp.cs's own OptStr/ReqStr/OptLong were exactly this shape,
//      UNattributed and zero-caller repo-wide, and were removed rather than suppressed
//      (resharper-clt-step5a-mcp-contract-surface)).
//   3. A point `[UsedImplicitly]`/`[PublicAPI]`/etc. annotation (`JetBrains.Annotations`,
//      `PrivateAssets="all"`, see Directory.Build.props) or a `// ReSharper disable <Rule>` file
//      header, at the declaration itself — ONE symbol or ONE file. For a false positive that is
//      neither solution-wide (1) nor zone-wide (2).
//
// All three are visible to a human in Rider, not just to `jb` on this gate — that visibility is
// exactly the property the next mechanism lacked.
//
// A FOURTH mechanism — a home-grown `suppressions` array/baseline in THIS file, matched by
// rule-id + file-path-suffix — used to live here and is deliberately GONE, not merely unused:
// don't add it back. Its only claimed justification was "these specific EXISTING findings are
// accepted, a NEW one of the same shape still fails the gate" — but the match was `s.RuleId ==
// ruleId && file.EndsWith(s.PathSuffix)`, with no line number and no occurrence count, so it could
// not actually tell an accepted finding from a new one of the same rule in the same file: a fresh
// instance would have matched and been silently swallowed exactly like the original. That was its
// one reason to exist over mechanisms 1-3, and it didn't hold. It was also invisible in Rider
// (JetBrains tooling has no idea this array exists), while 1-3 all are. If a real
// occurrence-pinned baseline is ever needed — one that actually distinguishes "this exact finding"
// from "a new finding of the same shape" — that is NEW work (real fingerprinting: line, snippet
// hash, or similar), not a reason to resurrect this array as-is.
//
// resharper-clt-step3-defect-shaped (2026-07-29, main c8b918ff) raised PossibleMultipleEnumeration
// and PossibleUnintendedQueryableAsEnumerable to ERROR in PetBox.slnx.DotSettings and individually
// read every finding each produced (5 + 3). Both were confirmed false positives (a pre-materialized
// ILookup grouping re-enumerated 2-3 times in TasksService; a linq2db ITable<T>.Select(...) handed
// straight to a FluentAssertions terminal .Should() in two test files) — but neither got a
// suppression: both shapes have a trivial, equally-correct rewrite that satisfies the analyzer
// instead of arguing with it (`.ToList()` once, either on the lookup read or before `.Should()`),
// so the code was changed instead. Keep reaching for a rewrite first, mechanisms 1-3 second; there
// is no baseline of last resort here any more.
//
// POLICY: every resharper-severity KEY (mechanism 2 above) lives ONLY in the ROOT .editorconfig,
// never in a nested one and never duplicated into PetBox.slnx.DotSettings. Two reasons, both
// load-bearing:
//   - The root .editorconfig is already in the gate's cache-home hash (`settingsFiles` below); a
//     nested tests/.editorconfig would NOT be, and jb would silently serve a stale cache on a
//     change there — the exact bug this file's --caches-home section exists to prevent.
//   - `.editorconfig` severity has HIGHER priority than `.DotSettings` — a stray severity line
//     dropped into .DotSettings for an unrelated reason can silently defeat a gate raise the
//     .editorconfig layer didn't anticipate (e.g. a rule bumped to ERROR there stops firing if a
//     later .editorconfig edit, made for a different rule, happens to widen a glob over it). One
//     surface for this class of setting means one place a reviewer has to check.

// ---- exit codes ---------------------------------------------------------------------------------
// 1 and 2 both block a push (`.githooks/pre-push` execs this and git rejects on any non-zero), and
// nothing anywhere parses the number — the split exists so a HUMAN reading the last line can tell
// "your code has a problem" from "the check never ran". Conflating them is what sent this repo
// hunting for a defect in a checkout that was innocent.
const int ExitCouldNotVerify = 2;

// ---- args -------------------------------------------------------------------------------------
var solution = "PetBox.slnx";
var severity = "ERROR";
string? reportPath = null;
var useLock = true;
var lockTimeout = TimeSpan.FromMinutes(30);
string? cachesHomeOverride = null;
for (var i = 0; i < args.Length; i++)
{
	var (name, inlineValue) = SplitArg(args[i]);
	string Value() => inlineValue ?? (i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value"));
	switch (name)
	{
		case "--solution": solution = Value(); break;
		case "--severity": severity = Value(); break;
		case "--report": reportPath = Value(); break;
		case "--no-lock": useLock = false; break;
		case "--caches-home": cachesHomeOverride = Value(); break;
		case "--lock-timeout":
			if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lockMinutes) || lockMinutes <= 0)
			{
				Console.Error.WriteLine("inspect-gate: --lock-timeout needs a positive number of minutes");
				return 1;
			}
			lockTimeout = TimeSpan.FromMinutes(lockMinutes);
			break;
		default:
			Console.Error.WriteLine($"inspect-gate: unknown argument '{args[i]}'");
			return 1;
	}
}

// ---- get a SARIF report -------------------------------------------------------------------
string sarifPath;
var isTempReport = false;
if (reportPath is not null)
{
	sarifPath = reportPath;
	if (!File.Exists(sarifPath))
	{
		Console.Error.WriteLine($"inspect-gate: --report file not found: {sarifPath}");
		return ExitCouldNotVerify;
	}
}
else
{
	sarifPath = Path.Combine(Path.GetTempPath(), $"petbox-inspectcode-{Guid.NewGuid():N}.sarif");
	isTempReport = true;

	// ---- caches-home, keyed to the settings that actually drive analysis --------------------
	// `jb inspectcode` keeps its own persistent solution cache under
	// %LOCALAPPDATA%\JetBrains\Transient\InspectCode\v262\SolutionCaches\_<solution>.* by default,
	// and that cache is keyed by solution identity — NOT by the content of .editorconfig, the
	// PetBox.slnx.DotSettings layer, or an ExternalAnnotations/ directory. Edit any of those and
	// rerun with the default cache, and jb happily hands back yesterday's findings: the settings
	// change looks like a no-op, which is exactly the bug that cost a full debugging session
	// before it was traced to the cache (see PetBox.slnx.DotSettings for the writeup). The fix is
	// to give jb a --caches-home whose PATH changes exactly when the settings that matter change,
	// and stays put otherwise:
	//   - edit .editorconfig, PetBox.slnx.DotSettings, or any file under ExternalAnnotations/ ->
	//     hash changes -> new, empty cache dir -> this run pays full solution-wide analysis, but
	//     sees the edit.
	//   - unchanged settings -> same hash -> same dir -> jb reuses its warm cache -> much faster.
	// The directory lives under the OS temp folder (not %LOCALAPPDATA%) so it is disposable and
	// never mistaken for the thing that needs to be committed.
	//
	// ExternalAnnotations/ was NOT fingerprinted at all until
	// resharper-clt-suppression-via-annotations added it to the list below: a directory's worth of
	// files is enumerated fresh on every run (not baked into a static array) so an added, removed,
	// or edited file under it changes the hash exactly like an edit to .editorconfig does — a
	// missing directory contributes zero paths rather than throwing.
	//
	// The CHECKOUT PATH is deliberately NOT in this hash, so every worktree on this machine shares
	// one caches-home. That looks like a collision and is not one: JetBrains already partitions
	// this directory by solution path INTERNALLY, one `_<solution>.<pathhash>.NN` subdirectory per
	// checkout. Measured 2026-08-28 (gate-runs-collide-across-worktrees): the shared home held 40
	// such subdirectories, ~48MB each; running the gate from a brand-new worktree added exactly one
	// more (`_PetBox.-544141864.00`) and left the other 40 untouched. So putting the path in this
	// hash would NOT remove any cross-worktree interference — there is none at this layer — it
	// would only replace one 2.1GB directory with N nearly identical copies of it and force a cold,
	// full-solution analysis the first time each worktree runs. Keep the hash keyed to settings
	// CONTENT only; JetBrains handles the per-path split.
	var externalAnnotationsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution)) ?? ".", "ExternalAnnotations");
	var settingsFiles = new[]
	{
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution)) ?? ".", ".editorconfig"),
		Path.GetFullPath(solution) + ".DotSettings",
	}.Concat(Directory.Exists(externalAnnotationsDir)
		? Directory.EnumerateFiles(externalAnnotationsDir, "*", SearchOption.AllDirectories)
		: [])
	.ToArray();
	// --caches-home overrides the computed path outright (CI wants a stable, cacheable
	// location outside the OS temp folder, which GitHub Actions' actions/cache does not
	// persist across runs anyway); the settings-hash computation above still runs so the
	// override path is validated the same way, but its result is discarded when an override
	// is given. The default (no override) is unchanged: keyed to settings CONTENT, disposable,
	// shared across worktrees on this machine.
	var cachesHome = cachesHomeOverride ?? Path.Combine(Path.GetTempPath(), $"petbox-inspectcode-cache-{HashOf(settingsFiles)}");

	// ---- version pin --------------------------------------------------------------------------
	// `jb` is a globally installed dotnet tool, not something this repo's manifest pulls in (see
	// the top-of-file comment on why .config/dotnet-tools.json is not the fix: the installed
	// jetbrains.resharper.globaltools package is ~978MB and the manifest is repo-wide, so
	// `dotnet tool restore` would drag it into CI too, which never runs inspectcode). That means
	// the gate's verdict otherwise rides on whatever `jb` happens to be on the machine that runs
	// it — a new JetBrains release can add or reweight inspections, and the same unchanged code
	// starts failing (or silently stops failing) depending solely on install luck, one machine at
	// a time. Pinning and checking here turns that invisible drift into a loud, immediate error.
	// NOTE: `jb inspectcode --version` prints only a two-part number ("2026.2"), while `dotnet
	// tool list -g` shows the full three-part package version ("2026.2.0") — jb's own banner does
	// not expose the patch digit, so this pin (and the exact-match check below) can only catch
	// major.minor drift, not patch-level drift. That's a real gap, not a choice; there is no
	// version string from jb itself finer-grained than this to compare against.
	const string ExpectedJbVersion = "2026.2";

	var actualJbVersion = GetJbVersion(out var jbNotFoundForVersionCheck);
	if (jbNotFoundForVersionCheck)
	{
		Console.Error.WriteLine("inspect-gate: 'jb' not found on PATH. Install with:");
		Console.Error.WriteLine("  dotnet tool install -g JetBrains.ReSharper.GlobalTools");
		return ExitCouldNotVerify;
	}
	if (actualJbVersion != ExpectedJbVersion)
	{
		Console.Error.WriteLine($"inspect-gate: jb version mismatch — expected {ExpectedJbVersion}, found {actualJbVersion ?? "(unparseable `jb inspectcode --version` output)"}.");
		Console.Error.WriteLine($"  Install the expected version:  dotnet tool update -g JetBrains.ReSharper.GlobalTools --version {ExpectedJbVersion}.0");
		Console.Error.WriteLine($"  If {(actualJbVersion is null ? "the installed version" : actualJbVersion)} is actually fine to use, update ExpectedJbVersion in scripts/inspect-gate.cs to match — it's exactly one line — after confirming the survivor set doesn't change.");
		return ExitCouldNotVerify;
	}

	var psi = new ProcessStartInfo("jb") { UseShellExecute = false };
	psi.ArgumentList.Add("inspectcode");
	psi.ArgumentList.Add(solution);
	psi.ArgumentList.Add($"--severity={severity}");
	psi.ArgumentList.Add("-f=Sarif");
	psi.ArgumentList.Add($"-o={sarifPath}");
	psi.ArgumentList.Add($"--caches-home={cachesHome}");
	// Without this jb narrates every file it touches — ~2200 lines on this solution, which in a
	// pre-push hook buries the one line that matters. WARN still surfaces jb's own failures.
	psi.ArgumentList.Add("--verbosity=WARN");

	// ---- defence 1: never share MSBuild worker nodes with another process ---------------------
	// `jb inspectcode` runs `MSBuild.exe ... /t:Restore /m:30` and MSBuild's node reuse is ON by
	// default, which makes those worker nodes MACHINE-GLOBAL: MSBuild will happily adopt idle
	// nodes another worktree's tooling left behind, and will leave its own behind for someone else
	// to adopt. Both halves are how a false red gets manufactured — an adopted node lives in a
	// FOREIGN process tree, so the watchdog killing that unrelated agent takes the node down
	// mid-restore and this gate reports MSB4166 for a checkout it never touched.
	//
	// This env var (MSBuild reads it directly; there is no `jb` flag for it, and jb hardcodes the
	// /m:30) turns both halves off: the nodes this run uses are its own children, and they exit
	// with it. Measured 2026-08-28: a plain `MSBuild -t:Restore -m:4` left 3 idle
	// `/nodemode:1 /nodeReuse:true` processes behind, and the same command with this variable set
	// left 0. It is NOT a substitute for the lock below — it stops this run from being poisoned by
	// a stranger's nodes, not from competing with a second gate for the machine's RAM.
	psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

	// ---- defence 2: one inspectcode on this machine at a time --------------------------------
	// Each `jb inspectcode` fans out to /m:30 MSBuild workers plus the ReSharper backend. Four
	// agents pushing at once is four of those, and node processes then start dying of resource
	// pressure — the same MSB4166, from a direction node reuse cannot fix. A named `Global\` mutex
	// is the right primitive here rather than a lock FILE: the OS releases it when the owner dies,
	// so a watchdog-killed agent cannot wedge the gate shut. That release surfaces as
	// AbandonedMutexException in the next waiter, which means "the previous owner died holding it,
	// it is yours now" — acquired, not failed. See TryAcquireGateLock.
	//
	// Everything here fails OPEN. This gate stands between an agent and `git push`, so a bug in
	// the locking must never be able to block a push that would otherwise pass: if the mutex
	// cannot even be created we warn and run unlocked, and the wait is bounded by --lock-timeout
	// (default 30m, several times the ~160s a run takes) rather than being infinite.
	// `Global\` (not `Local\`) so it spans terminal sessions and logon sessions: the agents this
	// serialises are separate processes in separate shells, and a per-session lock would not see
	// each other at all.
	const string GateLockName = @"Global\petbox-inspect-gate";

	Mutex? gateLock = null;
	var gateLockHeld = false;
	if (useLock)
	{
		try
		{
			gateLock = new Mutex(false, GateLockName);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
		{
			// Failing open on purpose: an unavailable mutex is a reason to lose serialisation, not
			// a reason to refuse the push.
			Console.WriteLine($"==> warning: could not open the gate lock ({ex.GetType().Name}: {ex.Message}); running WITHOUT machine-global serialisation.");
		}

		if (gateLock is not null)
		{
			var waited = Stopwatch.StartNew();
			gateLockHeld = TryAcquireGateLock(gateLock, TimeSpan.Zero);
			if (!gateLockHeld)
			{
				// Printed, and printed again periodically, because a silent wait is indistinguishable
				// from a hang — the agent (or human) staring at this needs to see progress.
				Console.WriteLine($"==> another inspect-gate run holds {GateLockName}; waiting for it (timeout {lockTimeout.TotalMinutes:F0}m)...");
				while (!gateLockHeld && waited.Elapsed < lockTimeout)
				{
					gateLockHeld = TryAcquireGateLock(gateLock, TimeSpan.FromSeconds(15));
					if (!gateLockHeld)
						Console.WriteLine($"    ...still waiting for the gate lock, {waited.Elapsed.TotalSeconds:F0}s elapsed");
				}

				if (!gateLockHeld)
				{
					Console.Error.WriteLine($"inspect-gate: gave up after {waited.Elapsed.TotalMinutes:F0}m waiting for the machine-global gate lock ({GateLockName}).");
					Console.Error.WriteLine("  Another inspect-gate run has held it that long, which is far longer than the ~160s a run takes.");
					Console.Error.WriteLine("  Look for a stuck `jb`/`MSBuild.exe` process and kill it (the lock frees itself when its owner dies),");
					Console.Error.WriteLine("  or re-run with --lock-timeout=<minutes>, or with --no-lock to skip the lock entirely.");
					return 1;
				}

				Console.WriteLine($"==> gate lock acquired after {waited.Elapsed.TotalSeconds:F0}s");
			}
		}
	}

	// Printed after the lock, not before it: announcing the command and only then blocking for
	// minutes reads like the inspection itself has hung.
	Console.WriteLine($"==> jb inspectcode {solution} --severity={severity} --caches-home={cachesHome}");

	// ---- run it, retrying a TOOL failure (never a finding) ------------------------------------
	// "jb could not run" and "your code has findings" are different facts and must not share an
	// exit path. Findings are judged below, from the SARIF, and a non-zero jb exit means the
	// inspection produced NO verdict at all. Since the dominant cause of that here is a transient
	// environment event (see the MSB4166 note above — a node pool shared with the rest of the
	// machine), a couple of retries convert most of these false reds into the correct verdict;
	// a neighbouring session's manual retry a minute later passed cleanly, which is the same thing
	// done by hand. Retrying costs nothing when the tool is genuinely broken: it fails 3x and says so.
	const int MaxAttempts = 3;
	var retryDelay = TimeSpan.FromSeconds(15);
	var exitCode = 0;
	try
	{
		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			try
			{
				using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
				proc.WaitForExit();
				exitCode = proc.ExitCode;
			}
			catch (Win32Exception)
			{
				Console.Error.WriteLine("inspect-gate: 'jb' not found on PATH. Install with:");
				Console.Error.WriteLine("  dotnet tool install -g JetBrains.ReSharper.GlobalTools");
				return ExitCouldNotVerify;
			}

			if (exitCode == 0) break;

			if (attempt < MaxAttempts)
			{
				Console.Error.WriteLine($"inspect-gate: jb inspectcode exited {exitCode} — the TOOL failed to run; this is not a finding in your code.");
				Console.Error.WriteLine($"  Retrying in {retryDelay.TotalSeconds:F0}s (attempt {attempt + 1} of {MaxAttempts}).");
				Thread.Sleep(retryDelay);
			}
		}
	}
	finally
	{
		// Held across the retries — they are one gate run — but not across the SARIF judging below,
		// which costs milliseconds and needs no exclusion, so the next agent starts as early as it can.
		if (gateLockHeld) gateLock!.ReleaseMutex();
		gateLock?.Dispose();
	}

	if (exitCode != 0)
	{
		// FAIL-CLOSED, but honestly: we block the push because the code was never inspected, NOT
		// because anything is wrong with it. Saying "N findings survived" here would be a lie, and
		// the old message's guess at the cause ("a `dotnet build` in THIS checkout") sent people
		// looking in the one place that is usually innocent.
		Console.Error.WriteLine($"inspect-gate: COULD NOT VERIFY — jb inspectcode failed to run {MaxAttempts} times (last exit {exitCode}).");
		Console.Error.WriteLine("  This is a failure of the INSPECTION TOOL, not a finding in your code: nothing was judged.");
		Console.Error.WriteLine("  Most likely cause: MSBuild's worker-node pool is shared by every process on this MACHINE, so a");
		Console.Error.WriteLine("  build or a killed agent in a DIFFERENT worktree can take this run's nodes down with it (MSB4166");
		Console.Error.WriteLine("  \"Child node ... exited prematurely\" above is exactly that). It is not necessarily anything in");
		Console.Error.WriteLine("  this checkout. Several heavy builds at once exhausting RAM does the same thing.");
		Console.Error.WriteLine("  Wait for the machine to go quiet and re-run, or push with:  git push --no-verify");
		return ExitCouldNotVerify;
	}
}

try
{
	// ---- parse + judge ----------------------------------------------------------------------
	var root = JsonNode.Parse(File.ReadAllText(sarifPath))?.AsObject()
		?? throw new InvalidOperationException("empty SARIF document");
	var results = (root["runs"]?.AsArray() ?? new JsonArray())
		.SelectMany(r => r?["results"]?.AsArray() ?? new JsonArray())
		.ToList();

	var survivors = new List<(string File, int Line, string RuleId, string Message)>();

	foreach (var resultNode in results)
	{
		if (resultNode is null) continue;
		var ruleId = (string?)resultNode["ruleId"] ?? "?";
		var message = (string?)resultNode["message"]?["text"] ?? "";
		var loc = resultNode["locations"]?[0]?["physicalLocation"];
		var file = NormalizeUri((string?)loc?["artifactLocation"]?["uri"] ?? "?");
		var line = (int?)loc?["region"]?["startLine"] ?? 0;

		survivors.Add((file, line, ruleId, message));
	}

	if (survivors.Count > 0)
	{
		foreach (var s in survivors.OrderBy(s => s.File).ThenBy(s => s.Line))
			Console.WriteLine($"{s.File}:{s.Line}  {s.RuleId}  {s.Message}");
		Console.Error.WriteLine($"inspect-gate: {survivors.Count} finding(s) survived ({results.Count} total).");
		return 1;
	}

	Console.WriteLine($"inspect-gate: clean — 0 findings survived ({results.Count} total).");
	return 0;
}
finally
{
	if (isTempReport) File.Delete(sarifPath);
}

// Asks the installed `jb` for its own version by running `jb inspectcode --version`, which
// prints a short banner and exits without touching a solution. Returns null (with notFound=true)
// if `jb` isn't on PATH at all — mirrors the Win32Exception handling around the real inspectcode
// invocation below, so a missing tool is reported the same way whether it's caught here first or
// there.
static string? GetJbVersion(out bool notFound)
{
	notFound = false;
	var psi = new ProcessStartInfo("jb") { UseShellExecute = false, RedirectStandardOutput = true };
	psi.ArgumentList.Add("inspectcode");
	psi.ArgumentList.Add("--version");

	string output;
	try
	{
		using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
		output = proc.StandardOutput.ReadToEnd();
		proc.WaitForExit();
	}
	catch (Win32Exception)
	{
		notFound = true;
		return null;
	}

	// Banner looks like:
	//   JetBrains Inspect Code 2026.2
	//   Running on x64 OS in x64 architecture, .NET 10.0.10 under Microsoft Windows 10.0.26200
	//   Version: 2026.2
	// — only the "Version:" line is parsed; the other two are free-form and not a stable contract.
	var versionLine = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Version:", StringComparison.Ordinal));
	return versionLine?["Version:".Length..].Trim();
}

// Waits up to `wait` for the machine-global gate lock. AbandonedMutexException is a SUCCESS here,
// not a failure: Windows raises it when the previous owner exited without releasing — exactly what
// happens when the watchdog kills an agent mid-gate — and the waiter that receives it now HOLDS the
// mutex. Swallowing it and returning true is what makes this lock self-healing; a lock file would
// instead need someone to notice and delete it by hand, turning one dead agent into a permanently
// wedged gate for everyone else.
static bool TryAcquireGateLock(Mutex gateLock, TimeSpan wait)
{
	try
	{
		return gateLock.WaitOne(wait);
	}
	catch (AbandonedMutexException)
	{
		Console.WriteLine("==> the previous gate-lock owner died without releasing it; taking the lock over.");
		return true;
	}
}

static (string Name, string? Value) SplitArg(string raw)
{
	var idx = raw.IndexOf('=');
	return idx < 0 ? (raw, null) : (raw[..idx], raw[(idx + 1)..]);
}

static string NormalizeUri(string uri)
{
	if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
		uri = new Uri(uri).LocalPath;
	return Uri.UnescapeDataString(uri).Replace('\\', '/');
}

// A short, stable fingerprint of the files that decide what inspectcode reports. A missing file
// still contributes a distinct value (rather than being skipped), so "the file didn't exist yet"
// and "the file is empty" hash differently too.
static string HashOf(IEnumerable<string> paths)
{
	using var sha = SHA256.Create();
	foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
	{
		var bytes = File.Exists(path) ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes($"<absent:{path}>");
		sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
	}
	sha.TransformFinalBlock([], 0, 0);
	return Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
}
