// inspect-gate — the gate `jb inspectcode` does not have on its own: a pass/fail threshold plus
// an explicit, version-controlled suppression list (a baseline). inspectcode has neither
// --fail-on-issues nor a baseline file; this script runs it, parses the SARIF report, subtracts
// the known-accepted findings below, and exits non-zero if anything else survives.
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
// Exit 0: nothing survived the suppression list (prints how many findings were suppressed).
// Exit 1: at least one finding survived (printed as `file:line  ruleId  message`), jb was not
//         found, the report is missing, or the SARIF failed to parse.

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

// ---- known suppressions (the baseline) -----------------------------------------------------
// One row per accepted false positive. Every row carries a reason in its own comment — this
// list is a liability, not a convenience, and each entry should be re-examined when the code
// it points at changes.
//
// This is deliberately EMPTY right now. It is a different tool from PetBox.slnx.DotSettings and
// the two are not interchangeable:
//   - PetBox.slnx.DotSettings (InspectionSeverities) turns a rule off (or down) EVERYWHERE, for
//     inspectcode AND Rider alike, because the rule itself is judged wrong for this codebase
//     (see the CS8602 entry there for a worked example, and why it lives there and not here).
//   - This array accepts SPECIFIC EXISTING findings — file+rule pairs already in the code today
//     — while leaving the rule at full severity for everything else, including new code. It is
//     the baseline: what to reach for when a rule is right in general but a handful of current
//     call sites are known-acceptable and you don't want the gate to fail on them today while
//     still wanting it to fail if a NEW instance of the same rule shows up elsewhere.
// Add a row here only when you've looked at that specific call site and decided it's fine, not
// as a way to silence a whole rule (that belongs in the .DotSettings file instead).
//
// resharper-clt-step3-defect-shaped (2026-07-29, main c8b918ff) raised PossibleMultipleEnumeration
// and PossibleUnintendedQueryableAsEnumerable to ERROR in PetBox.slnx.DotSettings and individually
// read every finding each produced (5 + 3). Both were confirmed false positives (a pre-materialized
// ILookup grouping re-enumerated 2-3 times in TasksService; a linq2db ITable<T>.Select(...) handed
// straight to a FluentAssertions terminal .Should() in two test files) — but a suppression row was
// NOT the right fix for either: both shapes have a trivial, equally-correct rewrite that satisfies
// the analyzer instead of arguing with it (`.ToList()` once, either on the lookup read or before
// `.Should()`), so the code was changed rather than the baseline grown. Keep reaching for a rewrite
// first; a suppression here is for cases where no such rewrite exists, not a first resort.
var suppressions = Array.Empty<Suppression>();

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
	// and that cache is keyed by solution identity — NOT by the content of .editorconfig or the
	// PetBox.slnx.DotSettings layer. Edit either file and rerun with the default cache, and jb
	// happily hands back yesterday's findings: the settings change looks like a no-op, which is
	// exactly the bug that cost a full debugging session before it was traced to the cache (see
	// PetBox.slnx.DotSettings for the writeup). The fix is to give jb a --caches-home whose PATH
	// changes exactly when the settings that matter change, and stays put otherwise:
	//   - edit .editorconfig or PetBox.slnx.DotSettings -> hash changes -> new, empty cache dir
	//     -> this run pays full solution-wide analysis, but sees the edit.
	//   - unchanged settings -> same hash -> same dir -> jb reuses its warm cache -> much faster.
	// The directory lives under the OS temp folder (not %LOCALAPPDATA%) so it is disposable and
	// never mistaken for the thing that needs to be committed.
	var settingsFiles = new[]
	{
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solution)) ?? ".", ".editorconfig"),
		Path.GetFullPath(solution) + ".DotSettings",
	};
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
	var suppressed = new List<(string File, int Line, Suppression Rule)>();

	foreach (var resultNode in results)
	{
		if (resultNode is null) continue;
		var ruleId = (string?)resultNode["ruleId"] ?? "?";
		var message = (string?)resultNode["message"]?["text"] ?? "";
		var loc = resultNode["locations"]?[0]?["physicalLocation"];
		var file = NormalizeUri((string?)loc?["artifactLocation"]?["uri"] ?? "?");
		var line = (int?)loc?["region"]?["startLine"] ?? 0;

		var suppression = suppressions.FirstOrDefault(s =>
			s.RuleId == ruleId && (s.PathSuffix is null || file.EndsWith(s.PathSuffix, StringComparison.Ordinal)));

		if (suppression is not null) { suppressed.Add((file, line, suppression)); continue; }
		survivors.Add((file, line, ruleId, message));
	}

	// The baseline is printed on EVERY run, green ones included. A suppression list nobody ever
	// sees is how a baseline rots into a list of forgotten bugs.
	foreach (var s in suppressed.OrderBy(s => s.File).ThenBy(s => s.Line))
		Console.WriteLine($"suppressed: {s.File}:{s.Line}  {s.Rule.RuleId} — {s.Rule.Reason}");

	if (survivors.Count > 0)
	{
		foreach (var s in survivors.OrderBy(s => s.File).ThenBy(s => s.Line))
			Console.WriteLine($"{s.File}:{s.Line}  {s.RuleId}  {s.Message}");
		Console.Error.WriteLine($"inspect-gate: {survivors.Count} finding(s) survived ({suppressed.Count} suppressed of {results.Count} total).");
		return 1;
	}

	Console.WriteLine($"inspect-gate: clean — 0 findings survived ({suppressed.Count} suppressed of {results.Count} total).");
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

sealed record Suppression(string RuleId, string? PathSuffix, string Reason);
