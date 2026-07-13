using PetBox.Core.Settings;
using PetBox.Tests.Support;

namespace PetBox.Tests.Settings;

// C#<->TS parity for the ui-state-framework cookie branch, without a codegen pipeline (the project
// has none, and isn't adding one): TsRecordSync reflects over the C# side and parses the
// hand-written TS interface, then compares them by key/type.
public sealed class UiStateTypeSyncTests
{
	static string RepoRootTsFile()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			var candidate = Path.Combine(dir, "src", "PetBox.Web", "ts", "ui-state.ts");
			if (File.Exists(candidate)) return candidate;
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("src/PetBox.Web/ts/ui-state.ts not found walking up from the test bin.");
	}

	// The REAL guard: PetBox.Core.Settings.BrowserState vs the hand-written ui-state.ts interface.
	// No longer vacuous as of work `sidebar-pin-server-state`: BrowserState.SidebarPinned is the
	// first [BrowserState] property, so this now actually compares a non-empty pair (previously
	// 0 vs 0). The synthetic fixtures below additionally prove the comparator fails loudly on a
	// missing property, an extra property, and a type mismatch — independent of what the real
	// pair currently declares.
	[Fact]
	public void BrowserState_MatchesTheHandWrittenTsInterface()
	{
		var tsSource = File.ReadAllText(RepoRootTsFile());

		var diffs = TsRecordSync.Diff(typeof(BrowserState), tsSource, "BrowserState");

		diffs.Should().BeEmpty(
			"BrowserState.cs and ui-state.ts must declare the same [BrowserState] keys/types — see the diff(s) above for what to fix");
	}

	// --- Synthetic fixtures proving the comparator fails loudly on real divergence ---

	public sealed record MatchingFixture
	{
		[BrowserState(Key = "sidebarPinned")]
		public bool SidebarPinned { get; init; }

		[BrowserState(Key = "lastTab")]
		public string LastTab { get; init; } = "";
	}

	const string MatchingTs = "export interface Fixture {\n\tsidebarPinned?: boolean;\n\tlastTab?: string;\n}\n";

	[Fact]
	public void Diff_MatchingPair_ReturnsEmpty()
	{
		TsRecordSync.Diff(typeof(MatchingFixture), MatchingTs, "Fixture").Should().BeEmpty();
	}

	[Fact]
	public void Diff_TsMissingAKnownCSharpProperty_ReportsIt()
	{
		const string ts = "export interface Fixture {\n\tsidebarPinned?: boolean;\n}\n";

		var diffs = TsRecordSync.Diff(typeof(MatchingFixture), ts, "Fixture");

		diffs.Should().ContainSingle(d => d.Contains("lastTab", StringComparison.Ordinal));
	}

	[Fact]
	public void Diff_TsHasAnExtraProperty_ReportsIt()
	{
		const string ts = "export interface Fixture {\n\tsidebarPinned?: boolean;\n\tlastTab?: string;\n\textra?: boolean;\n}\n";

		var diffs = TsRecordSync.Diff(typeof(MatchingFixture), ts, "Fixture");

		diffs.Should().ContainSingle(d => d.Contains("extra", StringComparison.Ordinal));
	}

	[Fact]
	public void Diff_TypeMismatch_ReportsIt()
	{
		const string ts = "export interface Fixture {\n\tsidebarPinned?: string;\n\tlastTab?: string;\n}\n";

		var diffs = TsRecordSync.Diff(typeof(MatchingFixture), ts, "Fixture");

		diffs.Should().ContainSingle(d => d.Contains("sidebarPinned", StringComparison.Ordinal) && d.Contains("boolean", StringComparison.Ordinal));
	}

	public sealed record EnumFixture
	{
		[BrowserState(Key = "mode")]
		public FixtureMode Mode { get; init; }
	}

	public enum FixtureMode { A, B }

	[Fact]
	public void Diff_Enum_MapsToTsString()
	{
		const string ts = "export interface Fixture {\n\tmode?: string;\n}\n";

		TsRecordSync.Diff(typeof(EnumFixture), ts, "Fixture").Should().BeEmpty();
	}

	// board-filters-server-state: CollapsedByBoard is the first Dictionary-shaped [BrowserState]
	// property — proves the comparator's Record<string, ...> mapping (added for it) round-trips
	// before relying on it against the real BrowserState/ui-state.ts pair below.
	public sealed record DictionaryFixture
	{
		[BrowserState(Key = "collapsedByBoard")]
		public Dictionary<string, string[]> CollapsedByBoard { get; init; } = new();
	}

	[Fact]
	public void Diff_Dictionary_MapsToTsRecord()
	{
		const string ts = "export interface Fixture {\n\tcollapsedByBoard?: Record<string, string[]>;\n}\n";

		TsRecordSync.Diff(typeof(DictionaryFixture), ts, "Fixture").Should().BeEmpty();
	}

	[Fact]
	public void Diff_Dictionary_TypeMismatch_ReportsIt()
	{
		const string ts = "export interface Fixture {\n\tcollapsedByBoard?: Record<string, number[]>;\n}\n";

		var diffs = TsRecordSync.Diff(typeof(DictionaryFixture), ts, "Fixture");

		diffs.Should().ContainSingle(d => d.Contains("collapsedByBoard", StringComparison.Ordinal));
	}
}
