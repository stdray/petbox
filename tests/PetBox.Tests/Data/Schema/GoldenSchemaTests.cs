using System.Text;
using PetBox.Core.Data;

namespace PetBox.Tests.Data.Schema;

// GOLDEN SCHEMA SNAPSHOTS — a safety net under the migration bodies.
//
// Each tier owns a FluentMigrator set (Core/Tasks/Memory/Sessions/Deploy). Nothing else in the
// suite pins the SHAPE those migrations produce: a body can be rewritten (raw SQL -> typed API,
// a column type widened, a partial index's predicate edited, a trigger dropped) and, unless some
// unrelated test happens to touch that exact column, the change lands silently.
//
// So: run the tier's migrations on a CLEAN temp database, take a normalized snapshot of the
// resulting schema (SchemaSnapshot — tables, columns, FKs, indexes WITH their partial WHERE,
// triggers, virtual tables) and diff it against a committed golden file. Any schema change now
// has to show up as a reviewable diff in the PR that causes it.
//
// The snapshot is normalized (see SchemaSnapshot) so it does NOT flip on formatting or on the
// raw-SQL -> typed-API rewrite. It flips when the SCHEMA changes. That is the whole point: a
// refactor of the migration bodies that preserves the schema leaves these files untouched.
//
// UPDATING A GOLDEN (when a migration is ADDED and the change is intended):
//
//     PETBOX_SCHEMA_GOLDEN_UPDATE=1 dotnet test tests/PetBox.Tests --filter GoldenSchema
//
// then `git diff tests/PetBox.Tests/Data/Schema` and READ IT. The update mode is a typist's
// convenience, not an approval: the diff is the review artifact. If a line you did not intend to
// change moved, the migration is wrong — not the golden. See README.md next to the files.
public sealed class GoldenSchemaTests : IDisposable
{
	const string UpdateEnvVar = "PETBOX_SCHEMA_GOLDEN_UPDATE";

	readonly string _dir;

	public GoldenSchemaTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-schema-golden-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	[Theory]
	[InlineData("core")]
	[InlineData("tasks")]
	[InlineData("memory")]
	[InlineData("sessions")]
	[InlineData("deploy")]
	public void Schema_matches_the_golden_snapshot(string tier)
	{
		var actual = Snapshot(tier, Path.Combine(_dir, tier + ".db"));
		var golden = GoldenPath(tier);

		if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
		{
			File.WriteAllText(golden, actual);
			return;
		}

		File.Exists(golden).Should().BeTrue(
			$"golden snapshot {golden} must be committed — regenerate with {UpdateEnvVar}=1");

		var expected = File.ReadAllText(golden);
		if (Normalize(expected) == Normalize(actual)) return;

		Assert.Fail($"""
			The {tier} schema no longer matches its golden snapshot.

			{Diff(Normalize(expected), Normalize(actual))}

			If the change is INTENDED, regenerate and review the diff:
			  {UpdateEnvVar}=1 dotnet test tests/PetBox.Tests --filter GoldenSchema
			  git diff {GoldenDir()}
			""");
	}

	// Migrations are forward-only and gated by VersionInfo: a second MigrateUp on the same file
	// must find nothing to do. If a migration body were to (re)apply itself — an `IF NOT EXISTS`
	// masking a duplicate DDL, an Execute.Sql seeding rows on every pass — the schema would
	// drift under a plain restart. Pin it: two runs, one snapshot.
	[Theory]
	[InlineData("core")]
	[InlineData("tasks")]
	[InlineData("memory")]
	[InlineData("sessions")]
	[InlineData("deploy")]
	public void Running_the_migrations_twice_leaves_the_schema_unchanged(string tier)
	{
		var db = Path.Combine(_dir, tier + ".db");
		var first = Snapshot(tier, db);
		var second = Snapshot(tier, db); // same file — VersionInfo says "already applied"

		second.Should().Be(first);
	}

	// The harness's own smoke test. A snapshot that silently stops capturing partial predicates,
	// triggers or virtual tables still passes every golden comparison (it just compares less) —
	// and the safety net is gone without a single red test. So assert that the things the PRAGMAs
	// alone cannot see are actually IN there.
	[Fact]
	public void Snapshot_captures_partial_indexes_triggers_and_virtual_tables()
	{
		var tasks = Snapshot("tasks", Path.Combine(_dir, "tasks.db"));
		var memory = Snapshot("memory", Path.Combine(_dir, "memory.db"));

		// Partial indexes — the backbone of the temporal model (at most one ACTIVE revision).
		tasks.Should().Contain("INDEX ux_plan_nodes_active_board_key UNIQUE origin=c (Board, Key) WHERE ActiveTo IS NULL");
		tasks.Should().Contain("WHERE ClosedAt IS NULL"); // M014 relations indexes
		memory.Should().Contain("INDEX ux_memory_entries_active_key UNIQUE origin=c (Store, Key) WHERE ActiveTo IS NULL");

		// Triggers (M014 registry derivation) — sqlite_master only.
		tasks.Should().Contain("TRIGGER trg_plan_nodes_register_id ");
		tasks.Should().Contain("TRIGGER trg_plan_nodes_unregister_id ");

		// FKs with their referential action.
		tasks.Should().Contain("FK (FromNodeId) -> plan_node_ids(NodeId) ON DELETE CASCADE");

		// Virtual tables (FTS5) are declared...
		memory.Should().Contain("VIRTUAL TABLE search_fts");
		memory.Should().Contain("USING fts5(");
		// ...and their derived shadow tables are NOT listed (noise).
		memory.Should().NotContain("TABLE search_fts_data");
		memory.Should().NotContain("TABLE search_fts_idx");
		memory.Should().NotContain("TABLE search_fts_config");

		// VersionInfo is part of the schema (its CONTENT — the applied version numbers — is not).
		tasks.Should().Contain("TABLE VersionInfo");
		tasks.Should().Contain("COL Version ");
		tasks.Should().NotContain("AppliedOn 2"); // no ROW data (applied versions/timestamps) in the dump
	}

	// ── plumbing ───────────────────────────────────────────────────────────────

	// Runs `tier`'s migration set against `dbPath` (created if absent) and snapshots the result.
	// Uses the tier's real entry point, so the harness also covers "which assembly does this tier
	// actually scan".
	static string Snapshot(string tier, string dbPath)
	{
		var cs = $"Data Source={dbPath}";
		switch (tier)
		{
			case "core": MigrationRunner.Run(cs); break;
			case "tasks": PetBox.Tasks.Data.TasksSchema.Ensure(cs); break;
			case "memory": PetBox.Memory.Data.MemorySchema.Ensure(cs); break;
			case "sessions": PetBox.Sessions.Data.SessionsSchema.Ensure(cs); break;
			case "deploy": PetBox.Deploy.Data.DeploySchema.Ensure(cs); break;
			default: throw new ArgumentOutOfRangeException(nameof(tier), tier, "unknown tier");
		}
		return Header(tier) + SchemaSnapshot.Capture(cs);
	}

	static string Header(string tier) => $"""
		# PetBox schema golden snapshot — tier: {tier}
		# Generated from the tier's FluentMigrator migration set on a CLEAN database.
		# Do not hand-edit. Regenerate: {UpdateEnvVar}=1 dotnet test tests/PetBox.Tests --filter GoldenSchema
		# ...then READ the diff. See README.md.

		""";

	static string GoldenDir()
	{
		var dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (File.Exists(Path.Combine(dir, "PetBox.slnx")))
				return Path.Combine(dir, "tests", "PetBox.Tests", "Data", "Schema");
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException("PetBox.slnx not found walking up from the test bin directory");
	}

	static string GoldenPath(string tier) => Path.Combine(GoldenDir(), tier + ".schema.txt");

	// Line endings and trailing blanks are git's business, not the schema's.
	static string Normalize(string text) =>
		string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
			.Select(l => l.TrimEnd()))
			.TrimEnd('\n');

	// A readable diff: WHICH lines vanished and WHICH appeared, not "the strings differ".
	// The snapshot's lines are self-describing and deterministically ordered, so a per-line
	// set-difference (in file order) already reads like a changelog of the schema.
	static string Diff(string expected, string actual)
	{
		var exp = expected.Split('\n');
		var act = actual.Split('\n');
		var expSet = exp.ToHashSet(StringComparer.Ordinal);
		var actSet = act.ToHashSet(StringComparer.Ordinal);

		var sb = new StringBuilder();
		foreach (var line in exp.Where(l => l.Length > 0 && !actSet.Contains(l)))
			sb.Append("- ").Append(line).Append('\n');
		foreach (var line in act.Where(l => l.Length > 0 && !expSet.Contains(l)))
			sb.Append("+ ").Append(line).Append('\n');

		return sb.Length == 0
			? "(only whitespace/ordering differs — regenerate the golden)"
			: "  '-' = in the golden but GONE from the schema, '+' = NEW in the schema:\n" + sb;
	}
}
