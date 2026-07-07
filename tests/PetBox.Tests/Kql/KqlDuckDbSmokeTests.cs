using System.Globalization;
using Kusto.Language;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Query;

namespace PetBox.Tests.Kql;

// DuckDb backend smoke + cross-backend parity. DuckDb is NOT yet in KqlBackendConfig.Active (the shared
// differential suite is still Sqlite-only until the flip wave), so this file targets KqlBackend.DuckDb
// DIRECTLY: it seeds the same dataset into a DuckDb `:memory:` LogDb (KqlLogHost) and a Sqlite one, runs
// one query per [Sql.Expression] shim family, and asserts BOTH that the DuckDb result is sane AND that it
// EQUALS the Sqlite result (the real check — every DuckDB arm must agree with the live SQLite path).
public sealed class KqlDuckDbSmokeTests
{
	// Small dataset exercising every shim family: numeric/string bag values, a JSON array (mv-expand),
	// well-formed AND malformed conversion inputs (tolong/todouble/tobool/todatetime), and datetimes
	// spanning day/week/month/year boundaries for startof*.
	static readonly IReadOnlyList<LogEntryRecord> Data =
	[
		Rec(1, new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc), 2, "alpha start 200",
			"""{"code":"200","chars":250,"ratio":"2.5","ok":"true","tags":["a","b"],"at":"2026-03-10"}"""),
		Rec(2, new DateTime(2026, 2, 20, 12, 0, 0, DateTimeKind.Utc), 4, "boom error 42",
			"""{"code":"500","chars":10,"ratio":"1.0","ok":"false","tags":["a"],"at":"not-a-date"}"""),
		Rec(3, new DateTime(2026, 3, 25, 23, 59, 0, DateTimeKind.Utc), 3, "beta warn",
			"""{"code":"abc","tags":["b","c"]}"""),
		Rec(4, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 4, "gamma crash 7",
			"""{"code":"404","chars":42,"ratio":"3.5","ok":"true","tags":[],"at":"2026-04-01T06:00:00Z"}"""),
	];

	static LogEntryRecord Rec(long id, DateTime tsUtc, int level, string msg, string props) => new()
	{
		Id = id,
		ServiceKey = "svc",
		TimestampMs = new DateTimeOffset(tsUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
		Level = level,
		Message = msg,
		MessageTemplate = msg,
		PropertiesJson = props,
		TemplateHash = 0,
	};

	// ---- parity: run the SAME query on Sqlite and DuckDb; column names (in order) and normalized rows
	// (order-insensitive) must be identical. Returns the DuckDb rows so a caller can add value asserts. ----
	static async Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> AssertParityAsync(string kql)
	{
		var code = KustoCode.Parse(kql);
		var sqlite = await KqlTestHost.ExecuteAsync(Data, KustoCode.Parse(kql), KqlBackend.Sqlite, options: OptFor(KqlBackend.Sqlite));
		var duck = await KqlTestHost.ExecuteAsync(Data, code, KqlBackend.DuckDb, options: OptFor(KqlBackend.DuckDb));

		duck.Columns.Select(c => c.Name).Should()
			.Equal(sqlite.Columns.Select(c => c.Name), "DuckDb and Sqlite must project the same columns for `{0}`", kql);
		Normalize(duck.Rows).Should()
			.BeEquivalentTo(Normalize(sqlite.Rows), "DuckDb must agree with Sqlite row-for-row for `{0}`", kql);
		return duck;
	}

	static KqlTranslationOptions OptFor(KqlBackend b) => new()
	{
		Dialect = b == KqlBackend.DuckDb ? KqlDialect.DuckDb : KqlDialect.Sqlite,
	};

	// Rows → order-insensitive multiset of pipe-joined canonical-string rows (so int/long boxing and
	// double spelling never cause false divergence, while genuine value differences still surface).
	static List<string> Normalize(IEnumerable<object?[]> rows) =>
		rows.Select(r => string.Join(" | ", r.Select(Cell))).OrderBy(s => s, StringComparer.Ordinal).ToList();

	static string Cell(object? v) => v switch
	{
		null => "∅",
		DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
		bool b => b ? "true" : "false",
		double d => d.ToString("R", CultureInfo.InvariantCulture),
		float f => f.ToString("R", CultureInfo.InvariantCulture),
		IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
		_ => v.ToString() ?? "",
	};

	// Convenience: DuckDb-only run (for value asserts already covered by parity, but read directly).
	static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> DuckAsync(string kql) =>
		KqlTestHost.ExecuteAsync(Data, KustoCode.Parse(kql), KqlBackend.DuckDb, options: OptFor(KqlBackend.DuckDb));

	// ---- where / project ----
	[Fact]
	public async Task Where_Project_Parity()
	{
		var (cols, rows) = await AssertParityAsync("events | where Level == 4 | project Id, Message | order by Id asc");
		cols.Select(c => c.Name).Should().Equal("Id", "Message");
		rows.Select(r => (long)r[0]!).Should().Equal(2L, 4L);
	}

	// ---- regex family: matches regex + extract + has ----
	[Fact]
	public async Task Regex_Extract_Has_Parity()
	{
		await AssertParityAsync("events | where Message matches regex 'error [0-9][0-9]' | project Id");
		await AssertParityAsync("events | where Message has 'boom' | project Id");
		var (_, rows) = await AssertParityAsync(
			"events | project Id, Num = extract('([0-9]+)', 1, Message) | order by Id asc");
		rows.Select(r => (string?)r[1]).Should().Equal("200", "42", "", "7");
	}

	// ---- typed conversions: tolong / todouble / tobool / todatetime (well-formed AND malformed→null) ----
	[Fact]
	public async Task Conversions_Parity_WithMalformedNulls()
	{
		var (cols, rows) = await AssertParityAsync(
			"""
			events
			| project Id, L = tolong(Properties.code), D = todouble(Properties.ratio),
			          B = tobool(Properties.ok), T = todatetime(Properties.at)
			| order by Id asc
			""");
		cols.Select(c => c.Name).Should().Equal("Id", "L", "D", "B", "T");

		// tolong: "200"/"500"/"404" parse; "abc" → null.
		rows.Select(r => (long?)r[1]).Should().Equal(200L, 500L, null, 404L);
		// todouble: "2.5"/"1.0"/"3.5"; missing (row 3) → null.
		rows.Select(r => (double?)r[2]).Should().Equal(2.5, 1.0, null, 3.5);
		// tobool: "true"/"false"; missing → null.
		rows.Select(r => (bool?)r[3]).Should().Equal(true, false, null, true);
		// todatetime: valid date / malformed→null / missing→null / ISO-Z.
		rows.Select(r => (DateTime?)r[4]).Should().Equal(
			new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), null, null,
			new DateTime(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc));
	}

	// ---- bin(Timestamp, 1h) + bin(numeric, N) ----
	[Fact]
	public async Task Bin_Datetime_And_Numeric_Parity()
	{
		var (_, rows) = await AssertParityAsync(
			"events | project Id, H = bin(Timestamp, 1h), B = bin(Id, 2) | order by Id asc");
		// bin(Timestamp,1h): row1 08:30→08:00.
		((DateTime)rows[0][1]!).Should().Be(new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc));
		// bin(Id,2): 1→0, 2→2, 3→2, 4→4.
		rows.Select(r => (long)r[2]!).Should().Equal(0L, 2L, 2L, 4L);
	}

	// ---- summarize count() by ... ----
	[Fact]
	public async Task Summarize_CountBy_Parity()
	{
		var (_, rows) = await AssertParityAsync("events | summarize C = count() by Level | order by Level asc");
		// Level 2:1, 3:1, 4:2. (Level key is logical int; count() is long.)
		rows.Select(r => ((int)r[0]!, (long)r[1]!)).Should().Equal((2, 1L), (3, 1L), (4, 2L));
	}

	// ---- summarize sum(...) — the BIGINT→HUGEINT materialization risk the flip task must know about ----
	[Fact]
	public async Task Summarize_SumId_Parity()
	{
		var (_, rows) = await AssertParityAsync("events | summarize S = sum(Id)");
		((long?)rows[0][0]).Should().Be(10L); // 1+2+3+4
	}

	[Fact]
	public async Task Summarize_SumBy_Parity()
	{
		var (_, rows) = await AssertParityAsync("events | summarize S = sum(Id) by Level | order by Level asc");
		// Level key is logical int; sum(Id) is long?. Levels 2→1, 3→3, 4→(2+4)=6.
		rows.Select(r => ((int)r[0]!, (long?)r[1])).Should().Equal((2, 1L), (3, 3L), (4, 6L));
	}

	// ---- mv-expand over a Properties JSON array (empty array → 0 rows = Kusto drop) ----
	[Fact]
	public async Task MvExpand_Tags_Parity()
	{
		var (cols, rows) = await AssertParityAsync(
			"events | mv-expand Properties.tags | project Id, tags | order by Id asc, tags asc");
		cols.Select(c => c.Name).Should().Equal("Id", "tags");
		// id1=[a,b], id2=[a], id3=[b,c], id4=[] (empty array drops) → 5 rows.
		rows.Select(r => ((long)r[0]!, (string?)r[1]))
			.Should().Equal((1L, "a"), (1L, "b"), (2L, "a"), (3L, "b"), (3L, "c"));
	}

	// ---- startof day / week / month / year ----
	[Fact]
	public async Task StartOf_Parity()
	{
		var (_, rows) = await AssertParityAsync(
			"""
			events
			| project Id, D = startofday(Timestamp), W = startofweek(Timestamp),
			          M = startofmonth(Timestamp), Y = startofyear(Timestamp)
			| order by Id asc
			""");
		// Row1 ts = 2026-01-15 (Thu). startofday=01-15; startofweek Sunday=01-11; startofmonth=01-01; startofyear=01-01.
		((DateTime)rows[0][1]!).Should().Be(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
		((DateTime)rows[0][2]!).Should().Be(new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc));
		((DateTime)rows[0][3]!).Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
		((DateTime)rows[0][4]!).Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
	}

	// ---- Properties bag access (JsonExtract) ----
	[Fact]
	public async Task PropertiesBag_JsonExtract_Parity()
	{
		var (_, rows) = await AssertParityAsync(
			"events | project Id, Code = Properties.code, Chars = Properties.chars | order by Id asc");
		rows.Select(r => (string?)r[1]).Should().Equal("200", "500", "abc", "404");
		// JSON number rendered as canonical text by the bag extract (same on both dialects).
		rows.Select(r => (string?)r[2]).Should().Equal("250", "10", null, "42");
	}
}
