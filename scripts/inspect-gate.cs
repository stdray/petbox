// inspect-gate — the gate `jb inspectcode` does not have on its own: a pass/fail threshold.
// inspectcode has no --fail-on-issues; this script runs it, parses the SARIF report, and exits
// non-zero if anything survives at the given severity. See the doctrine comment below for how a
// confirmed false positive gets out of the survivor set — it is never done in THIS file.
//
// This is the pre-push gate (.githooks/pre-push -> this script). Cost: ~45-110s wall-clock
// (measured on this repo; depends on warm/cold JetBrains caches), and nothing else may build in
// this checkout while it runs (inspectcode exits nonzero if a concurrent `dotnet build` is
// touching the same checkout).
//
// Activate: git config core.hooksPath .githooks
// Bypass:   git push --no-verify
//
// Usage:
//   dotnet run scripts/inspect-gate.cs                                       # full run, ERROR severity
//   dotnet run scripts/inspect-gate.cs -- --severity=WARNING                 # widen the gate
//   dotnet run scripts/inspect-gate.cs -- --solution Other.slnx
//   dotnet run scripts/inspect-gate.cs -- --report path/to/existing.sarif    # skip the jb run, just re-judge a report
//
// Exit 0: nothing survived at the given severity.
// Exit 1: at least one finding survived (printed as `file:line  ruleId  message`), jb was not
//         found, the report is missing, or the SARIF failed to parse.

using System.ComponentModel;
using System.Diagnostics;
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
//      ModuleExtensions.cs/ModuleMcp.OptLong/ReqStr, which sit in the same files and ARE real
//      dead-code candidates).
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

// ---- args -------------------------------------------------------------------------------------
var solution = "PetBox.slnx";
var severity = "ERROR";
string? reportPath = null;
for (var i = 0; i < args.Length; i++)
{
	var (name, inlineValue) = SplitArg(args[i]);
	string Value() => inlineValue ?? (i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} needs a value"));
	switch (name)
	{
		case "--solution": solution = Value(); break;
		case "--severity": severity = Value(); break;
		case "--report": reportPath = Value(); break;
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
		return 1;
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
	var externalAnnotationsDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution)) ?? ".", "ExternalAnnotations");
	var settingsFiles = new[]
	{
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution)) ?? ".", ".editorconfig"),
		Path.GetFullPath(solution) + ".DotSettings",
	}.Concat(Directory.Exists(externalAnnotationsDir)
		? Directory.EnumerateFiles(externalAnnotationsDir, "*", SearchOption.AllDirectories)
		: [])
	.ToArray();
	var cachesHome = Path.Combine(Path.GetTempPath(), $"petbox-inspectcode-cache-{HashOf(settingsFiles)}");

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
		return 1;
	}
	if (actualJbVersion != ExpectedJbVersion)
	{
		Console.Error.WriteLine($"inspect-gate: jb version mismatch — expected {ExpectedJbVersion}, found {actualJbVersion ?? "(unparseable `jb inspectcode --version` output)"}.");
		Console.Error.WriteLine($"  Install the expected version:  dotnet tool update -g JetBrains.ReSharper.GlobalTools --version {ExpectedJbVersion}.0");
		Console.Error.WriteLine($"  If {(actualJbVersion is null ? "the installed version" : actualJbVersion)} is actually fine to use, update ExpectedJbVersion in scripts/inspect-gate.cs to match — it's exactly one line — after confirming the survivor set doesn't change.");
		return 1;
	}

	Console.WriteLine($"==> jb inspectcode {solution} --severity={severity} --caches-home={cachesHome}");

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

	int exitCode;
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
		return 1;
	}
	if (exitCode != 0)
	{
		Console.Error.WriteLine($"inspect-gate: jb inspectcode exited {exitCode} (a `dotnet build` running concurrently in this checkout is the usual cause — nothing else may build here while this runs).");
		return 1;
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
