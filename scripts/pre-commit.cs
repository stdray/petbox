// pre-commit — whitespace-only autofix, nothing more.
//
// Runs `dotnet format whitespace` on the staged .cs files that are actually part of
// PetBox.slnx, then re-stages what it touched. No build, no test, no jb: those are
// pre-push's job (.githooks/pre-push -> scripts/inspect-gate.cs, ~45-110s) and CI's job.
// This hook exists to make the common case (docs, yml, json, a handful of .cs files) cost
// milliseconds-to-low-seconds, not minutes — the old pre-commit ran
// format --verify-no-changes + build + test on the whole solution on every commit, which is
// why hooks got disabled in this repo in the first place. Don't grow this file back into that.
//
// This is a straight port of the bash `.githooks/pre-commit` (git-blame it for history) to a
// file-based dotnet app, in the style of scripts/inspect-gate.cs / scripts/trx-timings.cs. The
// bash version's solution-membership step piped project paths through
// `xargs -n1 dirname | sort -u` — an earlier draft of that pipeline used `sort -z` on
// newline-delimited input. `sort -z` treats its whole input as ONE NUL-terminated record
// instead of one record per line, so it collapsed all ~22 project directories into a single
// entry. Every staged .cs file then failed the (silently broken) membership check, the
// nothing-to-format path fired, and the hook exited 0 having formatted nothing — visually
// identical to a successful no-op run. That whole class of bug disappears here: there is no
// intermediate CLI text pipeline for a delimiter convention (NUL vs newline) to leak across.
// PetBox.slnx is parsed directly into a HashSet<string>, and staged/unstaged file lists are a
// single NUL-split of one git command's output each — nothing pipes into anything else.
//
// Usage:
//   dotnet run scripts/pre-commit.cs          # normal invocation, from the .githooks/pre-commit shim
//
// Activate: git config core.hooksPath .githooks
// Bypass:   git commit --no-verify
//
// Exit 0: nothing to do, or everything staged formatted (and re-staged) cleanly.
// Exit nonzero: `dotnet format whitespace` (or a git command it depends on) failed; that exit
//               code is passed through.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

var repoRoot = Capture("git", ["rev-parse", "--show-toplevel"]).Trim();

// 1. Staged files (Added/Copied/Modified/Renamed — never Deleted, there's nothing to format in
// a deletion). NUL-delimited: the repo has paths a naive newline-split would break on.
var staged = SplitNul(Capture("git", ["diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z"], repoRoot));

if (staged.Length == 0)
	return 0;

// 2. Down to what `dotnet format whitespace` can actually change, twice over:
//
//   a) extension — verified empirically: it rewrites .cs, and leaves .cshtml/.md/.yml alone
//      (tested by injecting trailing whitespace into a .cshtml and running --include on it:
//      file came back byte-identical). Filtering here is what makes the docs/yml/json commit —
//      the overwhelmingly common case — exit before MSBuild ever loads.
//
//   b) solution membership — scripts/*.cs and build.cs are file-based dotnet apps (no
//      .csproj, not in PetBox.slnx). Passing a path outside the solution to --include was
//      verified to be harmless (exit 0, "Formatted 0 of N files", no warning even at
//      --verbosity diagnostic) — so this is not a correctness filter, dotnet format won't
//      choke or complain either way. It's still applied because letting one of these through
//      would silently skip the fast path above: a scripts/-only commit has a .cs file, so it
//      would survive filter (a) and pay the ~3.5s MSBuild-load tax for a file `dotnet format`
//      was always going to format 0 lines of anyway. Project directories are read straight out
//      of PetBox.slnx rather than hand-maintained, so this list can't drift from the solution.
var slnxPath = Path.Combine(repoRoot, "PetBox.slnx");
var projectDirs = Regex.Matches(File.ReadAllText(slnxPath), @"Project Path=""([^""]+)""")
	.Select(m => DirName(m.Groups[1].Value))
	.ToHashSet(StringComparer.Ordinal);

bool InSolution(string f) => projectDirs.Any(d => f.StartsWith(d + "/", StringComparison.Ordinal));

var candidates = staged.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && InSolution(f)).ToArray();

if (candidates.Length == 0)
	return 0;

// 3. Partially-staged files trap: a file with both staged and unstaged hunks would have its
// unstaged part swept into the commit by a blind `git add` after formatting. Detect the
// intersection with the unstaged-diff name list and drop those from the format run entirely —
// neither formatted nor re-added — rather than silently widening the commit or silently
// skipping with no explanation.
var unstaged = SplitNul(Capture("git", ["diff", "--name-only", "-z"], repoRoot)).ToHashSet(StringComparer.Ordinal);

var toFormat = candidates.Where(f => !unstaged.Contains(f)).ToArray();
var skipped = candidates.Where(f => unstaged.Contains(f)).ToArray();

if (skipped.Length > 0)
{
	Console.Error.WriteLine("pre-commit: skipping whitespace format for partially-staged file(s) (staged and");
	Console.Error.WriteLine("unstaged changes both present — formatting would drag the unstaged part into this");
	Console.Error.WriteLine("commit). CI still checks these; format them yourself before committing if you want");
	Console.Error.WriteLine("the fix in this commit:");
	foreach (var f in skipped)
		Console.Error.WriteLine($"  {f}");
}

if (toFormat.Length == 0)
	return 0;

// 4. Format, then re-stage exactly the files we handed to it. `git add` on a file `dotnet
// format` left untouched is a no-op, so this is safe to run unconditionally; the "did anything
// change" check below is purely to decide whether a message is warranted.
var formatArgs = new List<string> { "format", "whitespace", "PetBox.slnx", "--no-restore", "--include" };
formatArgs.AddRange(toFormat);
var formatExit = RunInherit("dotnet", formatArgs, repoRoot);
if (formatExit != 0)
	return formatExit;

var diffArgs = new List<string> { "diff", "--name-only", "-z", "--" };
diffArgs.AddRange(toFormat);
var changed = SplitNul(Capture("git", diffArgs, repoRoot));

var addArgs = new List<string> { "add", "--" };
addArgs.AddRange(toFormat);
var addExit = RunInherit("git", addArgs, repoRoot);
if (addExit != 0)
	return addExit;

if (changed.Length > 0)
{
	Console.WriteLine("pre-commit: reformatted whitespace and re-staged:");
	foreach (var f in changed)
		Console.WriteLine($"  {f}");
}

return 0;

static string DirName(string path)
{
	var idx = path.LastIndexOf('/');
	return idx < 0 ? "" : path[..idx];
}

static string[] SplitNul(string s) =>
	s.Split('\0', StringSplitOptions.RemoveEmptyEntries);

// Captures stdout as text; stderr flows straight through to the console (matches bash: only
// command-substitution output is captured, diagnostics are not).
static string Capture(string exe, IEnumerable<string> args, string? cwd = null)
{
	var psi = new ProcessStartInfo(exe)
	{
		UseShellExecute = false,
		RedirectStandardOutput = true,
		StandardOutputEncoding = Encoding.UTF8,
	};
	if (cwd is not null) psi.WorkingDirectory = cwd;
	foreach (var a in args) psi.ArgumentList.Add(a);
	using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
	var stdout = proc.StandardOutput.ReadToEnd();
	proc.WaitForExit();
	if (proc.ExitCode != 0)
		throw new InvalidOperationException($"{exe} {string.Join(' ', args)} exited {proc.ExitCode}");
	return stdout;
}

// Both stdout and stderr flow straight through to the console — the caller (and the person
// committing) needs to see `dotnet format`'s own progress/errors live.
static int RunInherit(string exe, IEnumerable<string> args, string cwd)
{
	var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = cwd };
	foreach (var a in args) psi.ArgumentList.Add(a);
	using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
	proc.WaitForExit();
	return proc.ExitCode;
}
