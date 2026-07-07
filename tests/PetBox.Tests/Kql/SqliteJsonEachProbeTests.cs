using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace PetBox.Tests.Kql;

// RESEARCH PROBE — SPIKE B (spec: kql-single-path-impl). The SQLite counterpart of the green DuckDB
// unnest probe (DuckDbLinq2DbProbeTests.P4): how does `events | mv-expand <jsonArrayCol>` translate on
// the ACTIVE SQLite backend? SQLite has no `unnest`; its table-valued equivalent is the builtin
// `json_each(json)` whose `value` column yields each array element, used as a correlated FROM source:
// `FROM log l, json_each(l.tags_json)`.
//
// Two facts capture the result:
//   Fact 1 (DirectForm) — the EXACT SelectMany/[Sql.TableExpression] shape that emitted clean SQL on
//     DuckDB is REJECTED by linq2db's SQLite provider (it classifies the correlated table-valued
//     function as an APPLY/LATERAL join, which the SQLite provider flags disallow; and even when forced,
//     linq2db renders the `LATERAL` keyword, which SQLite's parser does not accept). Documented as the
//     blocker, asserted so the suite still passes.
//   Fact 2 (FromSqlEscapeHatch) — linq2db's raw table-source facility (`FromSql`) emits SQLite's native
//     `FROM log, json_each(...)` correlated join, explodes correctly, AND composes further (Where/OrderBy
//     nest it as a derived table). This is the viable mv-expand implementation path on SQLite.
//
// Tagged Research so it never runs in the default `dotnet test` / CI path (see build.cs).
[Trait("Category", "Research")]
public sealed class SqliteJsonEachProbeTests : IDisposable
{
	readonly ITestOutputHelper _output;
	readonly string _tempDir;
	readonly string _dbPath;
	readonly DataConnection _db;

	[Table("log")]
	sealed class LogRow
	{
		[Column] public long Id { get; set; }
		[Column("tags_json")] public string TagsJson { get; set; } = "";
	}

	// The DuckDB-proven table expression, retargeted to SQLite's builtin. {2}=json column arg (correlated
	// per-row), {1}=alias. On DuckDB this rendered `INNER JOIN LATERAL unnest(...)`; SQLite's provider
	// rejects the APPLY classification outright (Fact 1).
	[Sql.TableExpression("json_each({2}) {1}")]
	static IQueryable<TagRow> JsonEachTags([ExprParameter] string json) =>
		throw new InvalidOperationException("linq2db table expression marker — never actually invoked in C#");

	sealed class TagRow
	{
		[Column("value")] public string Value { get; set; } = "";
	}

	// Result shape for the FromSql raw-source form (Fact 2). Column names match the SELECT aliases.
	sealed class Exploded
	{
		[Column("Id")] public long Id { get; set; }
		[Column("Value")] public string Value { get; set; } = "";
	}

	public SqliteJsonEachProbeTests(ITestOutputHelper output)
	{
		_output = output;

		// A temp-file SQLite DB (not :memory:) so schema/data survive linq2db's per-operation open/close,
		// exactly how SqliteKqlIntegrationTests seeds the production LogDb.
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-spikeB-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "probe.db");

		using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = """
				CREATE TABLE log(id INTEGER, tags_json TEXT);
				INSERT INTO log VALUES
					(1, '["a","b"]'),
					(2, '["a"]'),
					(3, '["b","c"]'),
					(4, '["c"]'),
					(5, '["a","c"]'),
					(6, '[]');
				""";
			cmd.ExecuteNonQuery();
		}

		_db = new DataConnection(new DataOptions().UseSQLite($"Data Source={_dbPath}"));
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	static readonly (long Id, string Value)[] ExpectedExploded =
	[
		(1L, "a"), (1L, "b"),
		(2L, "a"),
		(3L, "b"), (3L, "c"),
		(4L, "c"),
		(5L, "a"), (5L, "c"),
	];

	// FACT 1: the DuckDB-shaped SelectMany/TableExpression lateral is REJECTED by the SQLite provider.
	[Fact]
	public void JsonEach_DirectLateralForm_RejectedBySqliteProvider()
	{
		var q = _db.GetTable<LogRow>()
			.SelectMany(l => JsonEachTags(l.TagsJson), (l, t) => new { l.Id, t.Value });

		var ex = Assert.ThrowsAny<Exception>(() => q.ToList());
		_output.WriteLine("SPIKE B Fact 1 — SQLite provider rejected the lateral json_each form:");
		_output.WriteLine(ex.GetType().Name + ": " + ex.Message);

		// The blocker is the correlated APPLY/LATERAL classification, not json_each itself.
		ex.Message.Should().Contain("LATERAL");
		_output.WriteLine("SPIKE B Fact 1 VERDICT: CANNOT-EMIT via the SelectMany/[Sql.TableExpression] "
			+ "form — linq2db's SQLite provider does not support the correlated APPLY/LATERAL join the "
			+ "DuckDB provider used. (Blocker is the query-builder join classification, not SQLite: "
			+ "SQLite executes `FROM t, json_each(t.col)` natively — see Fact 2.)");
	}

	// FACT 2: linq2db's FromSql raw table source emits SQLite's native correlated json_each join,
	// explodes correctly, and composes further into a derived-table subquery.
	[Fact]
	public void JsonEach_ViaFromSqlRawTableSource_Explodes()
	{
		// One raw table source expressing the mv-expand: SQLite's native comma-join to json_each.
		// FromSql returns a composable IQueryable<Exploded> — linq2db wraps it as a derived table when
		// you chain further operators, so mv-expand stays inside ONE SQL query.
		var exploded = _db.FromSql<Exploded>(
			$"SELECT l.id AS Id, je.value AS Value FROM log l, json_each(l.tags_json) je");

		var all = exploded.ToList();
		_output.WriteLine("SPIKE B Fact 2 — FromSql json_each executed SQL:");
		_output.WriteLine(_db.LastQuery);
		_output.WriteLine($"SPIKE B Fact 2 — {all.Count} exploded rows:");
		foreach (var r in all.OrderBy(r => r.Id).ThenBy(r => r.Value))
			_output.WriteLine($"  {r.Id} -> {r.Value}");

		all.Select(r => (r.Id, r.Value)).Should().BeEquivalentTo(ExpectedExploded);

		// Prove it COMPOSES: a downstream where/order-by nests the json_each source as a derived table,
		// still ONE query — the shape a post-mv-expand pipeline stage needs.
		var composed = exploded.Where(x => x.Value != "a").OrderBy(x => x.Id).ThenBy(x => x.Value).ToList();
		_output.WriteLine("SPIKE B Fact 2 — composed (where Value != 'a' | order by) SQL:");
		_output.WriteLine(_db.LastQuery);
		composed.Select(r => (r.Id, r.Value)).Should().BeEquivalentTo(new[]
		{
			(1L, "b"), (3L, "b"), (3L, "c"), (4L, "c"), (5L, "c"),
		});

		_output.WriteLine("SPIKE B Fact 2 VERDICT: EMITS-CLEAN via FromSql — SQLite's native "
			+ "`FROM log, json_each(...)` explodes the array and composes into a derived-table subquery.");
	}
}
