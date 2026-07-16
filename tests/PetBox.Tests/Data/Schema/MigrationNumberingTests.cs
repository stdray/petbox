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
// HONEST LIMIT — this test does NOT catch the other half of the class, and that half already bit
// us (2026-07-16, this branch): reindex-as-first-class-mechanism DELETED M015_SlugInLexicalText,
// and the next slice added a NEW migration numbered 15. Both are self-consistent, so this test
// would stay green — but any file that ran the old 15 skips the new one forever and never grows
// the column (reproduced against such a file; Ensure does not throw, it fails later as "no such
// column: Key"). Catching THAT needs history, not reflection: a deleted migration's number is
// BURNED. See work/migration-number-reuse-is-silently-destructive.
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
}
