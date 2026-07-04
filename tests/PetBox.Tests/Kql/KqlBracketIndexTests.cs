using Kusto.Language;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Kql;

// Properties["any.key"] — bracket indexing with a string-literal selector, the canonical way to
// address a flat bag key. Real OTLP attribute keys are dotted (petbox.request_chars, http.route,
// service.name), so neither the bare-name fallback (identifiers can't contain dots) nor the old
// single-segment Properties.<key> path could reach them. These tests are PRODUCTION-ONLY (no
// KustoLoco differential): the reference executor models Properties as real dynamics — numbers stay
// numbers — while our flat bag yields the string-typed JSON value, the same representation contract
// KqlTypedPropertiesTests pins; a cell-level differential would compare "300" to 300. Coverage:
// where/project/extend/summarize-by/distinct/order over Properties["dotted.key"] on BOTH roots, typed
// conversions over indexed values, missing-key null semantics, dotted-path flattening
// (Properties.petbox.tool ≡ Properties["petbox.tool"]), and the structural error for a non-literal
// selector. SQLite pushdown parity lives in SqliteKqlIntegrationTests / SpanSqliteKqlIntegrationTests.
public sealed class KqlBracketIndexTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly DateTime Ts = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	// --- events root ---

	static LogEntryRecord Rec(long id, string props, string msg = "m") => new()
	{
		Id = id,
		ServiceKey = "svc",
		TimestampMs = new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds() + id * 1000,
		Level = (int)LogLevel.Information,
		Message = msg,
		MessageTemplate = msg,
		PropertiesJson = props,
	};

	static readonly LogEntryRecord[] Events =
	[
		Rec(1, """{"http.status_code":200,"http.route":"/a","region.code":"eu"}"""),
		Rec(2, """{"http.status_code":500,"http.route":"/b","region.code":"eu"}"""),
		Rec(3, """{"http.status_code":503,"http.route":"/b","region.code":"us"}"""),
		Rec(4, """{"plain":"x"}"""),
	];

	static IReadOnlyList<long> Ids(string kql) =>
		KqlTransformer.Apply(Events.AsQueryable(), Parse(kql)).ToList().Select(r => r.Id).ToList();

	static async Task<List<object?[]>> Table(string kql)
	{
		var result = KqlTransformer.Execute(Events.AsQueryable(), Parse(kql));
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	[Fact]
	public void Where_BracketIndexedDottedKey_Filters()
	{
		Ids("""events | where Properties["region.code"] == "eu" """).Should().BeEquivalentTo([1L, 2L]);
		Ids("""events | where Properties["http.route"] == "/b" """).Should().BeEquivalentTo([2L, 3L]);
	}

	[Fact]
	public void Where_TypedConversionOverIndexedValue_ComparesNumerically()
	{
		Ids("""events | where toint(Properties["http.status_code"]) >= 500""").Should().BeEquivalentTo([2L, 3L]);
		Ids("""events | where todouble(Properties["http.status_code"]) == 200""").Should().BeEquivalentTo([1L]);
	}

	[Fact]
	public async Task Project_IndexedValue_YieldsStringRepresentation()
	{
		// Same representation as bare/path access: a JSON number's raw text, a JSON string's text.
		var rows = await Table("""events | where Id == 2 | project Code = Properties["http.status_code"], Route = Properties["http.route"]""");
		rows.Should().ContainSingle();
		rows[0][0].Should().Be("500");
		rows[0][1].Should().Be("/b");
	}

	[Fact]
	public async Task Extend_IndexedValue_Computes()
	{
		var rows = await Table("""events | extend S = toint(Properties["http.status_code"]) | where S == 503 | project Id, S""");
		rows.Should().ContainSingle();
		rows[0][0].Should().Be(3L);
		rows[0][1].Should().Be(503L);
	}

	[Fact]
	public async Task SummarizeBy_IndexedKey_Groups()
	{
		var rows = await Table("""events | summarize Cnt = count() by Properties["region.code"]""");
		rows.Should().HaveCount(3);
		rows.Single(r => r[0] as string == "eu")[1].Should().Be(2L);
		rows.Single(r => r[0] as string == "us")[1].Should().Be(1L);
		rows.Single(r => r[0] is null)[1].Should().Be(1L); // missing key groups under null, like bare access
	}

	[Fact]
	public async Task Distinct_IndexedKey_DeDups()
	{
		var result = KqlTransformer.Execute(Events.AsQueryable(), Parse("""events | distinct Properties["region.code"]"""));
		result.Columns[0].Name.Should().Be("Properties.region.code");
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Select(r => r[0]).Should().BeEquivalentTo(["eu", "us", null]);
	}

	[Fact]
	public void OrderBy_IndexedKey_SortsPreSplit()
	{
		// Pre-split order by over the indexed value (nulls first ascending per SQLite/Kusto null order
		// is engine-specific; assert the non-null ordering).
		var ids = Ids("""events | where Id < 4 | order by Properties["http.status_code"] asc""");
		ids.Should().ContainInOrder(1L, 2L, 3L); // "200" < "500" < "503" (string order coincides here)
	}

	[Fact]
	public void MissingKey_IsNull_SameAsBareAccess()
	{
		// A missing key never matches any comparison (null semantics) — identical to bare-name access.
		Ids("""events | where Properties["absent.key"] == "x" """).Should().BeEmpty();
		Ids("""events | where toint(Properties["absent.key"]) == 1""").Should().BeEmpty();
	}

	[Fact]
	public void DottedPathForm_FlattensToSingleKey()
	{
		// Properties.region.code ≡ Properties["region.code"]: the bag is flat, the dotted remainder is
		// ONE key (never nested segments).
		Ids("events | where Properties.region.code == 'us'").Should().BeEquivalentTo([3L]);
		Ids("""events | where Properties.http.route == "/b" """).Should().BeEquivalentTo([2L, 3L]);
	}

	[Fact]
	public void NonLiteralSelector_ThrowsStructuralError()
	{
		var inWhere = () => KqlTransformer.Apply(Events.AsQueryable(), Parse("events | where Properties[Message] == 'x'")).ToList();
		inWhere.Should().Throw<UnsupportedKqlException>().WithMessage("*string literal*");

		var inProject = () => KqlTransformer.Execute(Events.AsQueryable(), Parse("events | project X = Properties[Message]"));
		inProject.Should().Throw<UnsupportedKqlException>().WithMessage("*string literal*");
	}

	[Fact]
	public void SearchKey_IsNormalized_LikeTheWriteBoundary()
	{
		// The write boundary stores `we"ird.key` as `we_ird.key` (KqlPropertyKeys); the search boundary
		// applies the same rule to the requested key, so the RAW spelling finds the normalized row.
		var rows = new[] { Rec(9, """{"we_ird.key":"v1"}""") };
		var ids = KqlTransformer.Apply(rows.AsQueryable(), Parse("events | where Properties[\"we\\\"ird.key\"] == 'v1'"))
			.ToList().Select(r => r.Id).ToList();
		ids.Should().BeEquivalentTo([9L]);
	}

	[Fact]
	public void BagValue_IntLiteral_TeachesBothWorkingForms()
	{
		// `Properties["k"] == 200` (int literal against the text bag) must not error into a trap: the
		// message steers to the string form (which WORKS — text parity pre/post split) and to
		// toint()/todouble() for numeric comparison.
		var act = () => KqlTransformer.Apply(Events.AsQueryable(), Parse("""events | where Properties["http.status_code"] == 500""")).ToList();
		act.Should().Throw<UnsupportedKqlException>()
			.WithMessage("*string literal*").WithMessage("*toint()*");
	}

	// --- value-representation parity contract (the live prod bug class): the same string-literal
	// predicate over a NUMERIC or BOOLEAN bag value must agree pre-split and post-split. Both roots;
	// the real-SQLite twins live in the integration suites. ---

	[Fact]
	public async Task NumericBagValue_StringLiteral_PreAndPostSplit_Agree()
	{
		var pre = Ids("""events | where Properties["http.status_code"] == "500" """);
		var post = (await Table("""events | extend one = 1 | where Properties["http.status_code"] == "500" | project Id"""))
			.Select(r => (long)r[0]!).ToList();
		pre.Should().BeEquivalentTo([2L]);
		post.Should().BeEquivalentTo(pre);
	}

	[Fact]
	public async Task BooleanBagValue_StringLiteral_PreAndPostSplit_Agree()
	{
		var rows = new[]
		{
			Rec(11, """{"flag.on":true}"""),
			Rec(12, """{"flag.on":false}"""),
		};
		var pre = KqlTransformer.Apply(rows.AsQueryable(), Parse("""events | where Properties["flag.on"] == "true" """))
			.ToList().Select(r => r.Id).ToList();
		pre.Should().BeEquivalentTo([11L]);

		var result = KqlTransformer.Execute(rows.AsQueryable(),
			Parse("""events | extend one = 1 | where Properties["flag.on"] == "true" | project Id"""));
		var post = new List<object?[]>();
		await foreach (var r in result.Rows) post.Add(r);
		post.Select(r => (long)r[0]!).Should().BeEquivalentTo(pre);
	}

	// --- spans root ---

	static SpanRecord Span(string spanId, string name, int startSec, int durMs, string attrs) => new()
	{
		SpanId = spanId,
		TraceId = "t1",
		Name = name,
		Kind = (int)SpanKind.Internal,
		StartUnixNs = (new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds() + startSec * 1000L) * 1_000_000L,
		EndUnixNs = (new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds() + startSec * 1000L + durMs) * 1_000_000L,
		StatusCode = (int)SpanStatusCode.Ok,
		AttributesJson = attrs,
	};

	static readonly SpanRecord[] Spans =
	[
		Span("s1", "mcp.tool tasks_search", 0, 120, """{"petbox.request_chars":250,"petbox.response_chars":9000,"petbox.tool":"tasks_search"}"""),
		Span("s2", "mcp.tool tasks_search", 1, 80, """{"petbox.request_chars":100,"petbox.response_chars":500,"petbox.tool":"tasks_search"}"""),
		Span("s3", "mcp.tool memory_search", 2, 60, """{"petbox.request_chars":40,"petbox.response_chars":700,"petbox.tool":"memory_search"}"""),
		Span("s4", "mcp.tool tasks_search", 3, 30, """{"petbox.request_chars":77,"petbox.response_chars":333,"petbox.tool":"tasks_search"}"""),
	];

	static async Task<List<object?[]>> SpanTable(string kql)
	{
		var result = KqlTransformer.ExecuteSpans(Spans.AsQueryable(), Parse(kql));
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	[Fact]
	public async Task Spans_Where_BracketIndexedAttribute_Filters()
	{
		var rows = await SpanTable("""spans | where Properties["petbox.tool"] == "memory_search" | project SpanId""");
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s3"]);
	}

	[Fact]
	public async Task Spans_SummarizeBy_IndexedAttribute_Groups()
	{
		var rows = await SpanTable("""spans | summarize Cnt = count() by Properties["petbox.tool"]""");
		var by = rows.ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["tasks_search"].Should().Be(3);
		by["memory_search"].Should().Be(1);
	}

	[Fact]
	public async Task Spans_NumericAttribute_StringLiteral_PreAndPostSplit_Agree()
	{
		var pre = await SpanTable("""spans | where Properties["petbox.request_chars"] == "250" | project SpanId""");
		var post = await SpanTable("""spans | extend one = 1 | where Properties["petbox.request_chars"] == "250" | project SpanId""");
		pre.Select(r => r[0]).Should().BeEquivalentTo(["s1"]);
		post.Select(r => r[0]).Should().BeEquivalentTo(pre.Select(r => r[0]));
	}

	// The exact live-reported query that motivated this fix (in-memory form; the SQLite form lives in
	// SpanSqliteKqlIntegrationTests.ReportedQuery_...).
	[Fact]
	public async Task Spans_ReportedRegressionQuery_Works()
	{
		var result = KqlTransformer.ExecuteSpans(Spans.AsQueryable(), Parse(
			"""
			spans | where Name == "mcp.tool tasks_search" | top 3 by Start desc
			| project Start, Name, Duration, req=todouble(Properties["petbox.request_chars"]), resp=todouble(Properties["petbox.response_chars"])
			"""));
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Start", "Name", "Duration", "req", "resp");

		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		rows.Should().HaveCount(3); // s4, s2, s1 by Start desc (s3 is a different tool)
		rows.Select(r => (string)r[1]!).Should().OnlyContain(n => n == "mcp.tool tasks_search");
		rows.Select(r => (double?)r[3]).Should().ContainInOrder(77d, 100d, 250d); // Start desc: s4, s2, s1
		rows.Select(r => (double?)r[4]).Should().ContainInOrder(333d, 500d, 9000d);
		rows[0][2].Should().Be(TimeSpan.FromMilliseconds(30)); // Duration flows through unchanged
	}
}
