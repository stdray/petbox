using System.Text.RegularExpressions;

namespace PetBox.Tests.Architecture;

// Guard for work `ui-state-single-mechanism-guard` (maintainer invariant: exactly ONE mechanism
// for client UI state survives). ui-state-framework built that one mechanism — BrowserState +
// IUiState (server) / ts/ui-state.ts's readUiState/writeUiState (client) against the single
// `petbox.ui` cookie: DB [Setting] = cross-device preference, [BrowserState] cookie = window/device
// state the server must see BEFORE it renders. Sidebar pin, theme and the KQL panel pin all moved
// onto it; two half-built stores were deleted. Nothing enforced "exactly one" until this test —
// without it, the invariant survives exactly until the next feature calls `localStorage.setItem`
// directly instead of `writeUiState`.
//
// WHY A TEXT SCAN, NOT NetArchTest (every other guard in this folder uses NetArchTest): NetArchTest
// reasons over compiled .NET types. ts/ is TypeScript — it is never compiled into the assemblies
// this test host loads, so there is no reflection surface to walk (the same reason
// DbInjectionGuardTests below composes DI directly instead of using NetArchTest). A text scan over
// the actual shipped source is the only thing that can see a raw localStorage/cookie call at all.
public sealed class UiStateSingleMechanismGuardTests
{
	const string FrameworkFile = "ui-state.ts";

	// ALLOWLIST — SHRINKS ONLY. Every entry is either live migration debt with a named follow-up,
	// or one deliberate, argued-for permanent exception. Never add an entry to make a NEW violation
	// pass — route new state through ts/ui-state.ts (readUiState/writeUiState) + a [BrowserState]
	// field instead. When a listed migration lands, DELETE the entry rather than leaving it "just in
	// case" — AllowlistEntries_AreStillNeeded below fails loudly on a stale one, so the deletion is
	// enforced, not merely asked for.
	static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		// board.ts's entry (tasksView:*/tasksActiveOnly/tasksCollapsed/tasksSort via localStorage)
		// is GONE as of work `board-view-cross-device` + `board-filters-server-state`: view mode/
		// tag-`by`/field selection moved to a per-(project,board) DB [Setting] (BrowserState.
		// BoardViewPreferences), active-only/sort to global DB [Setting]s (TasksActiveOnly/
		// TasksSortBy/TasksSortDesc), and the collapsed-node set to a per-board cookie key
		// (BrowserState.CollapsedByBoard, through ui-state.ts like everything else in the cookie
		// branch) — board.ts is now clean, the LAST holdout in this allowlist is closed, and the
		// maintainer's "exactly one mechanism" invariant has nothing left excepted from it.

		// petbox.pendingToast via sessionStorage — a message deferred across exactly ONE form
		// submit so the hotkey toast survives the htmx swap that follows Ctrl+Enter. Judgment call,
		// not migration debt: it never affects first paint (the server renders nothing from it), so
		// BrowserState's whole reason to exist — the server needs the value before it draws the
		// page — doesn't apply here. A reader who disagrees should argue it as a BrowserState field
		// instead of deleting this line unreviewed.
		["logs.ts"] = "petbox.pendingToast via sessionStorage — transient one-shot toast message, "
			+ "does not affect first paint; treated as a PERMANENT exception, not migration debt.",
	};

	static string TsDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src", "PetBox.Web", "ts");
			if (File.Exists(Path.Combine(candidate, FrameworkFile))) return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/PetBox.Web/ts (with ui-state.ts) not found walking up from the test bin.");
	}

	// Matches the actual browser-storage API surface — localStorage/sessionStorage as an
	// identifier, however accessed (bare, `window.`, `globalThis.`, or bracket-indexed — `\b`
	// matches the bare name regardless of what precedes it) — and `document.cookie` access
	// (whitespace-tolerant so `document . cookie` still counts). It deliberately does NOT require a
	// following `.getItem`/`.setItem` etc.: ts/logs.ts mentions the bare word "localStorage" in a
	// FOUC-bug comment, and that prose must not be mistaken for the identifier used to relocate the
	// comment-stripping problem instead of solving it. Comments are stripped BEFORE this pattern
	// ever runs (see StripComments) — that is what keeps the FOUC comment from tripping this.
	static readonly Regex ViolationPattern = new(
		@"\b(localStorage|sessionStorage)\b|document\s*\.\s*cookie",
		RegexOptions.Compiled);

	// Strips `//` line comments and `/* */` block comments before scanning. Deliberately naive — it
	// does not understand string/template literals that themselves contain "//" or "/*" — this is a
	// guardrail against an honest next feature, not a lexer defending against someone determined to
	// evade it (see class doc). No file actually in ts/ today has such a literal, so this is not a
	// live gap, only a known limit.
	static string StripComments(string source)
	{
		var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
		return Regex.Replace(noBlockComments, @"//[^\n]*", "");
	}

	// Scans every top-level `*.ts` file in ts/ (there are no subdirectories today; TopDirectoryOnly
	// is explicit so a future subfolder doesn't silently drop out of the sweep — it would need its
	// own decision, not a silent one). Test files (`*.test.ts`) are NOT skipped: they are scanned
	// exactly like product files, comments stripped the same way, because a raw storage call inside
	// a test that isn't mocking the DOM is just as much a second mechanism as one in product code —
	// and skipping them would have been the wrong call anyway once comment-stripping already handles
	// ui-state.test.ts's own prose mention of `document.cookie`.
	static IReadOnlyList<(string Name, string Code)> ScanTsFiles()
	{
		var dir = TsDir();
		return Directory.EnumerateFiles(dir, "*.ts", SearchOption.TopDirectoryOnly)
			.Select(path => (Name: Path.GetFileName(path), Code: StripComments(File.ReadAllText(path))))
			.ToList();
	}

	[Fact]
	public void NoRawBrowserStorage_OutsideUiStateTs()
	{
		var offenders = ScanTsFiles()
			.Where(f => !string.Equals(f.Name, FrameworkFile, StringComparison.OrdinalIgnoreCase))
			.Where(f => !Allowlist.ContainsKey(f.Name))
			.Where(f => ViolationPattern.IsMatch(f.Code))
			.Select(f => f.Name)
			.ToList();

		offenders.Should().BeEmpty(
			"client UI state has exactly ONE mechanism — BrowserState + IUiState + ts/ui-state.ts "
			+ "(readUiState/writeUiState against the single `petbox.ui` cookie). A raw localStorage/"
			+ "sessionStorage/document.cookie call outside ui-state.ts reintroduces a second one. "
			+ "Route the new state through writeUiState/readUiState (add a [BrowserState] field if the "
			+ "server needs it before first paint), or, if it genuinely cannot affect first paint, add "
			+ "a reasoned, migration-tracked entry to this test's Allowlist instead. Offenders: "
			+ string.Join(", ", offenders));
	}

	// The allowlist may only SHRINK. An entry that no longer matches anything is migration debt
	// someone already paid off and forgot to remove from this list — fail loudly so it gets deleted
	// instead of silently granting a blanket exemption nothing in the file needs any more.
	[Fact]
	public void AllowlistEntries_AreStillNeeded()
	{
		var files = ScanTsFiles().ToDictionary(f => f.Name, f => f.Code, StringComparer.OrdinalIgnoreCase);

		var stale = Allowlist.Keys
			.Where(name => !files.TryGetValue(name, out var code) || !ViolationPattern.IsMatch(code))
			.ToList();

		stale.Should().BeEmpty(
			"an allowlist entry with no matching raw storage/cookie call left in the file is stale — "
			+ "delete the entry (this guard only ever shrinks the allowlist), don't leave it. Stale: "
			+ string.Join(", ", stale));
	}

	// Guard-the-guard: if TsDir()/ScanTsFiles ever silently found nothing (a moved directory, a test
	// host that doesn't ship ts/ alongside the binaries), the two assertions above would pass by
	// vacuity and stop protecting anything.
	[Fact]
	public void TheGuard_ActuallyScansFiles()
	{
		var files = ScanTsFiles();

		files.Should().HaveCountGreaterThan(10, "the ts/ sweep must cover the real frontend source tree");
		files.Should().Contain(f => f.Name == FrameworkFile);
		files.Should().Contain(f => f.Name == "board.ts");
		files.Should().Contain(f => f.Name == "logs.ts");
	}
}
