using System.Reflection;
using System.Text.RegularExpressions;
using FluentMigrator;

namespace PetBox.Tests.Data.Schema;

// A migration's NUMBER is its identity: FluentMigrator records it in the file's VersionInfo table
// and skips any version already listed there. The class NAME (M016_SearchKeyColumn) is how humans
// read that identity — and nothing keeps the two in sync. Rename the file/class without touching
// the attribute (or vice versa) and every clean-database test still passes: GoldenSchemaTests
// builds from scratch, where the number only decides ORDER, never whether the migration runs.
// The lie only surfaces on a database that already carries a VersionInfo row — i.e. in prod.
//
// So pin the one thing reflection can prove: the number in the name IS the number in the attribute.
//
// REFLECTION'S HONEST LIMIT — the two theories above do NOT catch the other half of the class,
// and that half already bit us (2026-07-16, this branch): reindex-as-first-class-mechanism DELETED
// M015_SlugInLexicalText, and the next slice added a NEW migration numbered 15. Both are
// self-consistent, so reflection over the CURRENT assembly stays green — but any file that ran
// the old 15 skips the new one forever and never grows the column (reproduced against such a
// file; Ensure does not throw, it fails later as "no such column: Key"). Reflection cannot see a
// DELETED file, so catching this needs history, not reflection.
//
// That history lives in tests/PetBox.Tests/Data/Schema/used-migration-numbers/<tier>.txt — a
// checked-in, append-only registry of every number ever assigned in a tier, live or burned.
// RegistryNumbers_AreStrictlyIncreasing and LiveMigrations_MatchTheRegistryExactly below enforce
// it: a number is unique and only ever grows, including numbers that were burned by a deleted
// migration — a "no such column" surprise like M015's cannot happen again unnoticed. The legal
// gap this incident left behind (tasks: ...14, 16, no 15) stays legal — burned means "never
// reused", not "must be filled in". See work/migration-number-reuse-is-silently-destructive.
public sealed class MigrationNumberingTests
{
	static readonly Regex NamePrefix = new(@"^M(\d+)_", RegexOptions.Compiled);

	// One real type per tier — reflection walks ITS assembly, so a tier that grows a migration set
	// is covered the moment its assembly is named here.
	public static TheoryData<string, Type> Tiers() => new()
	{
		{ "core", typeof(PetBox.Core.Data.MigrationRunner) },
		{ "tasks", typeof(PetBox.Tasks.Data.TasksSchema) },
		{ "memory", typeof(PetBox.Memory.Data.MemorySchema) },
		{ "sessions", typeof(PetBox.Sessions.Data.SessionsSchema) },
		{ "deploy", typeof(PetBox.Deploy.Data.DeploySchema) },
	};

	[Theory]
	[MemberData(nameof(Tiers))]
	public void MigrationClassName_StatesTheSameNumberAsItsAttribute(string tier, Type anchor)
	{
		var migrations = Migrations(anchor).ToList();

		migrations.Should().NotBeEmpty($"{tier} must expose migrations — an empty set would make this theory vacuous");

		foreach (var (type, version) in migrations)
		{
			var m = NamePrefix.Match(type.Name);
			m.Success.Should().BeTrue($"{tier}: migration {type.Name} must be named M<number>_<what-it-does>");

			int.Parse(m.Groups[1].Value).Should().Be((int)version,
				$"{tier}: {type.Name} says {m.Groups[1].Value} in its name but {version} in [Migration] — "
				+ "the ATTRIBUTE is what VersionInfo records, so the name is lying to every reader");
		}
	}

	// Within one tier a number must be unique: two migrations sharing a version means one of them
	// never runs on a file that recorded the other.
	[Theory]
	[MemberData(nameof(Tiers))]
	public void MigrationNumbers_AreUniqueWithinTheTier(string tier, Type anchor)
	{
		var dupes = Migrations(anchor)
			.GroupBy(x => x.Version)
			.Where(g => g.Count() > 1)
			.Select(g => $"{g.Key}: {string.Join(" + ", g.Select(x => x.Type.Name))}")
			.ToList();

		dupes.Should().BeEmpty($"{tier} has migrations sharing a version number: {string.Join("; ", dupes)}");
	}

	static IEnumerable<(Type Type, long Version)> Migrations(Type anchor) =>
		anchor.Assembly.GetTypes()
			.Select(t => (Type: t, Attr: t.GetCustomAttribute<MigrationAttribute>()))
			.Where(x => x.Attr is not null)
			.Select(x => (x.Type, x.Attr!.Version));

	// ---------------------------------------------------------------------------------------
	// History-backed half: a checked-in registry of every number ever assigned in a tier, so a
	// DELETED migration's number stays visible to the test even though reflection cannot see it.
	// ---------------------------------------------------------------------------------------

	enum RegistryStatus { Live, Burned }

	sealed record RegistryEntry(int Number, RegistryStatus Status, string Name, int LineNumber);

	// Walk up from the test binary to the repo root — same technique DbLayerGuardTests.SrcDir()
	// uses, so the registry is read from the checked-in source tree, not copied build output.
	static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (Directory.Exists(Path.Combine(dir, "src", "PetBox.Web"))) return dir;
			dir = Path.GetDirectoryName(dir);
		}

		throw new DirectoryNotFoundException("repo root (src/PetBox.Web) not found walking up from the test bin.");
	}

	static string RegistryPath(string tier) =>
		Path.Combine(RepoRoot(), "tests", "PetBox.Tests", "Data", "Schema", "used-migration-numbers", $"{tier}.txt");

	// Format per non-comment line: "<number> <LIVE|BURNED> <ClassName> [# free-text reason]".
	static IReadOnlyList<RegistryEntry> Registry(string tier)
	{
		var path = RegistryPath(tier);
		File.Exists(path).Should().BeTrue($"{tier} must have a used-migration-numbers registry at {path}");

		var lines = File.ReadAllLines(path);
		var entries = new List<RegistryEntry>();

		for (var i = 0; i < lines.Length; i++)
		{
			var line = lines[i].Trim();
			if (line.Length == 0 || line.StartsWith('#')) continue;

			var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
			(parts.Length >= 3).Should().BeTrue(
				$"{tier}: registry line {i + 1} ('{line}') must read '<number> <LIVE|BURNED> <ClassName> [# reason]'");

			int.TryParse(parts[0], out var number).Should().BeTrue(
				$"{tier}: registry line {i + 1} has a non-numeric first field: '{parts[0]}'");

			var status = parts[1] switch
			{
				"LIVE" => RegistryStatus.Live,
				"BURNED" => RegistryStatus.Burned,
				_ => throw new InvalidOperationException(
					$"{tier}: registry line {i + 1} has status '{parts[1]}' — must be LIVE or BURNED"),
			};

			entries.Add(new RegistryEntry(number, status, parts[2], i + 1));
		}

		return entries;
	}

	// The registry is append-only: read top-to-bottom, numbers must strictly increase. This is
	// BOTH invariants the card asks for in one check — a repeated number breaks "strictly
	// increase" exactly as a smaller one does, so uniqueness and monotonic growth (including
	// against burned numbers, which occupy a line same as live ones) fall out of a single rule.
	[Theory]
	[MemberData(nameof(Tiers))]
	public void RegistryNumbers_AreStrictlyIncreasing(string tier, Type anchor)
	{
		_ = anchor;
		var entries = Registry(tier);
		entries.Should().NotBeEmpty($"{tier} registry must list at least the migrations the tier already has");

		for (var i = 1; i < entries.Count; i++)
		{
			entries[i].Number.Should().BeGreaterThan(entries[i - 1].Number,
				$"{tier}: registry line {entries[i].LineNumber} ({entries[i].Number} {entries[i].Name}) must come "
				+ $"after line {entries[i - 1].LineNumber} ({entries[i - 1].Number} {entries[i - 1].Name}) — the "
				+ "registry is append-only, and a number — live or burned — is never reused and never precedes "
				+ "one already on the books");
		}
	}

	// The registry is DATA, not documentation: it must exactly match the tier's live migrations
	// (same number, same class name), in both directions. That symmetric check is what makes a
	// forgotten registration fail (live migration absent from the LIVE lines) and what makes
	// number reuse fail (live migration's number already spoken for, by a BURNED line or by a
	// LIVE line naming something else) — without needing git history at test time.
	[Theory]
	[MemberData(nameof(Tiers))]
	public void LiveMigrations_MatchTheRegistryExactly(string tier, Type anchor)
	{
		var registry = Registry(tier);
		var byNumber = registry.ToLookup(e => e.Number);
		var live = Migrations(anchor).ToList();

		foreach (var (type, version) in live)
		{
			var number = (int)version;
			var candidates = byNumber[number].ToList();

			if (candidates.Any(e => e.Status == RegistryStatus.Live && e.Name == type.Name)) continue;

			var burned = candidates.FirstOrDefault(e => e.Status == RegistryStatus.Burned);
			if (burned is not null)
			{
				Assert.Fail(
					$"{tier}: {type.Name} reuses number {number}, which the registry BURNED as {burned.Name} "
					+ $"(used-migration-numbers/{tier}.txt line {burned.LineNumber}) — a burned number is never "
					+ "reused; give the new migration a fresh number above the tier's max instead.");
				continue;
			}

			var renamed = candidates.FirstOrDefault(e => e.Status == RegistryStatus.Live);
			if (renamed is not null)
			{
				Assert.Fail(
					$"{tier}: {type.Name} claims number {number}, but the registry's LIVE entry for that number "
					+ $"is {renamed.Name} (used-migration-numbers/{tier}.txt line {renamed.LineNumber}) — update "
					+ "that line's name to match instead of adding a new one; the number does not change.");
				continue;
			}

			Assert.Fail(
				$"{tier}: {type.Name} (number {number}) has no entry in the used-migration-numbers registry — "
				+ $"append 'LIVE {type.Name}' at the bottom of tests/PetBox.Tests/Data/Schema/used-migration-numbers/{tier}.txt "
				+ "with a number greater than everything already listed there.");
		}

		var liveByNumber = live.ToDictionary(x => (int)x.Version, x => x.Type.Name);
		foreach (var entry in registry.Where(e => e.Status == RegistryStatus.Live))
		{
			if (liveByNumber.TryGetValue(entry.Number, out var actualName) && actualName == entry.Name) continue;

			Assert.Fail(
				$"{tier}: used-migration-numbers/{tier}.txt line {entry.LineNumber} claims {entry.Name} (number "
				+ $"{entry.Number}) is LIVE, but no migration with that number and name exists in code — if it "
				+ "was deleted, flip this line to BURNED (with a reason) instead of removing it.");
		}
	}
}
