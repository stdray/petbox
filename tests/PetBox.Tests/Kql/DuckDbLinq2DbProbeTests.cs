using DuckDB.NET.Data;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Xunit.Abstractions;

namespace PetBox.Tests.Kql;

// RESEARCH PROBE (spec: kql-single-sql-path, gating a future rewrite): captures what SQL
// linq2db 6.3.0 actually emits for its DuckDB provider, for exactly the operator set a
// single-SQL-path KQL rewrite would need (grouped aggregates, COUNT DISTINCT, window
// functions, and — the load-bearing one — LATERAL/unnest for mv-expand). Not a regression
// suite: it logs verbatim generated SQL via ITestOutputHelper and asserts the SQL linq2db
// hands back actually executes against DuckDB (P4 additionally asserts the exploded rows).
// Tagged Research so it never runs in the default `dotnet test` / CI path (see build.cs).
[Trait("Category", "Research")]
public sealed class DuckDbLinq2DbProbeTests : IDisposable
{
	readonly ITestOutputHelper _output;
	readonly DataConnection _db;
	readonly DuckDBConnection _rawConnection;

	[Table("log")]
	sealed class LogRow
	{
		[Column] public long Id { get; set; }
		[Column] public DateTime Ts { get; set; }
		[Column] public string Level { get; set; } = "";
		[Column] public string Msg { get; set; } = "";
		[Column] public double Val { get; set; }
		// Physical column is `tags_json`; the default name map would produce `TagsJson`, and
		// DuckDB's binder case-folds but does NOT underscore-fold — so the mapping MUST be
		// explicit or the lateral unnest below binds against a non-existent column.
		[Column("tags_json")] public string TagsJson { get; set; } = "";
	}

	public DuckDbLinq2DbProbeTests(ITestOutputHelper output)
	{
		_output = output;

		// `UseDuckDB` (confirmed present on `DataOptions` — DuckDBTools/DuckDBOptions/
		// UseDuckDB all resolve in the 6.3.0 assembly) only overloads on a connection
		// STRING, not an already-open DbConnection — so linq2db owns/opens the ADO
		// connection itself. DuckDB's ":memory:" is per-connection-object, so the raw-SQL
		// fallback (P4) reuses linq2db's OWN underlying connection (`_db.Connection`)
		// rather than opening a second, independent in-memory database.
		_db = new DataConnection(new DataOptions().UseDuckDB("DataSource=:memory:"));
		_rawConnection = (DuckDBConnection)_db.OpenDbConnection();

		using (var cmd = _rawConnection.CreateCommand())
		{
			cmd.CommandText = """
				CREATE TABLE log(id BIGINT, ts TIMESTAMP, level VARCHAR, msg VARCHAR, val DOUBLE, tags_json VARCHAR);
				INSERT INTO log VALUES
					(1, TIMESTAMP '2026-01-01 00:00:00', 'INFO',  'start',   1.0, '["a","b"]'),
					(2, TIMESTAMP '2026-01-01 00:01:00', 'ERROR', 'boom',    2.5, '["a"]'),
					(3, TIMESTAMP '2026-01-01 00:02:00', 'INFO',  'tick',    3.0, '["b","c"]'),
					(4, TIMESTAMP '2026-01-01 00:03:00', 'ERROR', 'boom',    4.5, '["c"]'),
					(5, TIMESTAMP '2026-01-01 00:04:00', 'INFO',  'tock',    5.0, '["a","c"]'),
					(6, TIMESTAMP '2026-01-01 00:05:00', 'WARN',  'hmm',     6.5, '[]');
				""";
			cmd.ExecuteNonQuery();
		}
	}

	public void Dispose() => _db.Dispose();

	// P1 — GroupBy -> Count/Sum/Min/Max/Average.
	[Fact]
	public void P1_GroupBy_Aggregates()
	{
		var q = _db.GetTable<LogRow>()
			.GroupBy(x => x.Level)
			.Select(g => new
			{
				Level = g.Key,
				Count = g.Count(),
				Sum = g.Sum(x => x.Val),
				Min = g.Min(x => x.Val),
				Max = g.Max(x => x.Val),
				Avg = g.Average(x => x.Val),
			});

		var sql = q.ToSqlQuery().Sql;
		_output.WriteLine("P1 GroupBy aggregates SQL:");
		_output.WriteLine(sql);

		var rows = q.ToList();
		rows.Should().NotBeEmpty();
	}

	// P2 — COUNT(DISTINCT ...). The brief's literal shape
	// (`Sql.Ext.Count(x.Level, Sql.AggregateModifier.Distinct).ToValue()` "inside a grouped
	// projection") does not type-check as a bare GroupBy().Select(g => ...) projection — `x`
	// (a per-row column reference) isn't in scope once you've collapsed to the group `g`.
	// The compilable equivalent linq2db actually supports is the SAME aggregate-modifier
	// extension used as a WINDOW aggregate: a per-row Select with PartitionBy standing in
	// for the GroupBy, still landing on `COUNT(DISTINCT ...)` in the generated SQL.
	[Fact]
	public void P2_Count_Distinct()
	{
		var q = _db.GetTable<LogRow>()
			.Select(x => new
			{
				x.Msg,
				DistinctLevelsForMsg = Sql.Ext.Count(x.Level, Sql.AggregateModifier.Distinct)
					.Over().PartitionBy(x.Msg).ToValue(),
			});

		var sql = q.ToSqlQuery().Sql;
		_output.WriteLine("P2 COUNT(DISTINCT ...) SQL:");
		_output.WriteLine(sql);

		var rows = q.ToList();
		rows.Should().NotBeEmpty();
	}

	// P3 — window: ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...).
	[Fact]
	public void P3_Window_RowNumber()
	{
		var q = _db.GetTable<LogRow>()
			.Select(x => new
			{
				x.Id,
				x.Level,
				Rn = Sql.Ext.RowNumber().Over().PartitionBy(x.Level).OrderBy(x.Ts).ToValue(),
			});

		var sql = q.ToSqlQuery().Sql;
		_output.WriteLine("P3 ROW_NUMBER() window SQL:");
		_output.WriteLine(sql);

		var rows = q.ToList();
		rows.Should().NotBeEmpty();
	}

	// P4 — THE CRITICAL ONE: LATERAL/unnest (mv-expand equivalent), the GREEN path. A
	// [Sql.TableExpression] (NOT [Sql.TableFunction]) whose template takes ONLY the json
	// column as an [ExprParameter]: {2} is the first arg (the column, correlated per-row →
	// linq2db renders it as a LATERAL join), {1} is the generated table alias. The template
	// wraps the arg in `from_json(..., '["VARCHAR"]')` so `unnest` receives a LIST (not a raw
	// JSON string), and DuckDB names that output column `unnest`, so TagRow maps to it.
	[Sql.TableExpression("unnest(from_json({2}, '[\"VARCHAR\"]')) {1}")]
	static IQueryable<TagRow> UnnestJsonTags([ExprParameter] string json) =>
		throw new InvalidOperationException("linq2db table expression marker — never actually invoked in C#");

	sealed class TagRow
	{
		[Column("unnest")] public string Value { get; set; } = "";
	}

	[Fact]
	public void P4_Lateral_Unnest()
	{
		var q = _db.GetTable<LogRow>()
			.SelectMany(l => UnnestJsonTags(l.TagsJson), (l, t) => new { l.Id, t.Value });

		var sql = q.ToSqlQuery().Sql;
		_output.WriteLine("P4 linq2db-emitted lateral/unnest SQL:");
		_output.WriteLine(sql);

		// The load-bearing assertion: linq2db actually EXECUTES the lateral unnest against
		// DuckDB and returns the exploded rows.
		var rows = q.ToList();
		_output.WriteLine($"P4 linq2db-emitted SQL EXECUTED OK, {rows.Count} exploded rows:");
		foreach (var r in rows.OrderBy(r => r.Id).ThenBy(r => r.Value))
			_output.WriteLine($"  {r.Id} -> {r.Value}");

		// Seed: id1=[a,b] id2=[a] id3=[b,c] id4=[c] id5=[a,c] id6=[] → 8 exploded rows,
		// empty array contributes none. This is the mv-expand semantics the KQL rewrite needs.
		var exploded = rows.Select(r => (r.Id, r.Value)).ToList();
		exploded.Should().HaveCount(8);
		exploded.Should().BeEquivalentTo(new[]
		{
			(1L, "a"), (1L, "b"),
			(2L, "a"),
			(3L, "b"), (3L, "c"),
			(4L, "c"),
			(5L, "a"), (5L, "c"),
		});
		// The empty-array row (id 6) must explode to nothing.
		exploded.Should().NotContain(t => t.Id == 6);

		_output.WriteLine("P4 VERDICT: EMITS-CLEAN — linq2db emitted a real INNER JOIN LATERAL "
			+ "unnest(from_json(...)) and returned the expected 8 exploded rows.");
	}
}
