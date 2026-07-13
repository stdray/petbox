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
	// Vacuously green today — BrowserState ships with zero [BrowserState] properties by design (see
	// its doc comment: this work node is the mechanism, its five follow-ups add the fields). It
	// starts guarding for real the moment a follow-up adds a property to one side and forgets the
	// other. The synthetic fixtures below prove the comparator itself DOES fail loudly — the real
	// pair can't demonstrate that while it's empty on both sides.
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
}
