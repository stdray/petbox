using Kusto.Language;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Kql;

// The `spans` table root: the SAME KQL subset (the whole kql-coverage operator catalog) over a named
// log's Spans table. These are production-only (the reference executor has no spans table); they pin
// root routing, span column addressing (incl. Duration / Kind-Status name forms / Start-End as
// datetime), attribute access, a where/project/summarize/top pipeline, and structural-error parity with
// the events root. SQLite pushdown parity lives in SpanSqliteKqlIntegrationTests.
public sealed class SpanKqlTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly DateTime Base = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static SpanRecord Span(
		string spanId, string traceId, string name, SpanKind kind, DateTime start, TimeSpan duration,
		SpanStatusCode status = SpanStatusCode.Unset, string? parent = null, string attrs = "{}",
		string? statusDescription = null) => new()
		{
			SpanId = spanId,
			TraceId = traceId,
			ParentSpanId = parent,
			Name = name,
			Kind = (int)kind,
			StartUnixNs = new DateTimeOffset(start, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000L,
			EndUnixNs = (new DateTimeOffset(start, TimeSpan.Zero).ToUnixTimeMilliseconds() + (long)duration.TotalMilliseconds) * 1_000_000L,
			StatusCode = (int)status,
			StatusDescription = statusDescription,
			AttributesJson = attrs,
		};

	static readonly SpanRecord[] Spans =
	[
		Span("s1", "t1", "GET /a", SpanKind.Server, Base, TimeSpan.FromMilliseconds(100), SpanStatusCode.Ok, attrs: """{"status_code":200,"peer":"eu"}"""),
		Span("s2", "t1", "db.query", SpanKind.Client, Base.AddSeconds(1), TimeSpan.FromMilliseconds(400), SpanStatusCode.Error, parent: "s1", attrs: """{"status_code":500,"peer":"eu"}"""),
		Span("s3", "t2", "GET /b", SpanKind.Server, Base.AddSeconds(2), TimeSpan.FromMilliseconds(50), SpanStatusCode.Ok, attrs: """{"status_code":200,"peer":"us"}"""),
	];

	static IQueryable<SpanRecord> Src() => Spans.AsQueryable();

	static KqlResult Exec(string kql) => KqlTransformer.ExecuteSpans(Src(), Parse(kql));

	static async Task<List<object?[]>> Materialize(KqlResult result)
	{
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	static int Col(KqlResult r, string name)
	{
		for (var i = 0; i < r.Columns.Count; i++)
			if (r.Columns[i].Name == name) return i;
		throw new Xunit.Sdk.XunitException($"column '{name}' not found in [{string.Join(", ", r.Columns.Select(c => c.Name))}]");
	}

	// --- root routing + column shape ---

	[Fact]
	public void SpanRecordColumns_HaveExpectedSchema()
	{
		KqlTransformer.SpanRecordColumns.Select(c => c.Name).Should().ContainInOrder(
			"SpanId", "TraceId", "ParentSpanId", "Name", "Kind", "KindName",
			"Start", "End", "Duration", "Status", "StatusName", "StatusDescription", "PropertiesJson");
	}

	[Fact]
	public async Task BareSpans_YieldsFullSpanColumnShape()
	{
		var result = Exec("spans");
		result.Columns.Select(c => c.Name).Should().ContainInOrder(
			"SpanId", "TraceId", "ParentSpanId", "Name", "Kind", "KindName",
			"Start", "End", "Duration", "Status", "StatusName", "StatusDescription", "PropertiesJson");
		var rows = await Materialize(result);
		rows.Should().HaveCount(3);
	}

	[Fact]
	public void GetRootTableName_DistinguishesRoots()
	{
		KqlTransformer.GetRootTableName(Parse("spans | where Kind == 1")).Should().Be("spans");
		KqlTransformer.GetRootTableName(Parse("events | take 1")).Should().Be("events");
	}

	// --- column addressing: strings / ints / name forms / datetimes / duration ---

	[Fact]
	public async Task Project_AddressesAllTypedColumns()
	{
		var result = Exec("spans | where SpanId == 's2' | project TraceId, Name, Kind, KindName, Start, End, Duration, Status, StatusName");
		var rows = await Materialize(result);
		rows.Should().ContainSingle();
		var r = rows[0];
		r[Col(result, "TraceId")].Should().Be("t1");
		r[Col(result, "Name")].Should().Be("db.query");
		r[Col(result, "Kind")].Should().Be((int)SpanKind.Client);
		r[Col(result, "KindName")].Should().Be("Client");
		r[Col(result, "Start")].Should().Be(Base.AddSeconds(1));
		r[Col(result, "End")].Should().Be(Base.AddSeconds(1).AddMilliseconds(400));
		r[Col(result, "Duration")].Should().Be(TimeSpan.FromMilliseconds(400));
		r[Col(result, "Status")].Should().Be((int)SpanStatusCode.Error);
		r[Col(result, "StatusName")].Should().Be("Error");
	}

	[Fact]
	public void Project_ColumnTypes_MatchSchema()
	{
		var result = Exec("spans | project Start, Duration, Kind, KindName");
		result.Columns[Col(result, "Start")].ClrType.Should().Be<DateTime>();
		result.Columns[Col(result, "Duration")].ClrType.Should().Be<TimeSpan>();
		result.Columns[Col(result, "Kind")].ClrType.Should().Be<int>();
		result.Columns[Col(result, "KindName")].ClrType.Should().Be<string>();
	}

	// --- where over span columns (pre-split; runs over the in-memory IQueryable here, real SQL in the
	// integration suite) ---

	[Fact]
	public async Task Where_KindEquals_Filters()
	{
		var rows = await Materialize(Exec("spans | where Kind == 1")); // Server
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s1", "s3"]);
	}

	[Fact]
	public async Task Where_NameEquals_Filters()
	{
		var rows = await Materialize(Exec("spans | where Name == 'db.query'"));
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task Where_StartAsDatetime_Filters()
	{
		var rows = await Materialize(Exec("spans | where Start >= datetime(2026-04-19T10:00:01Z)"));
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s2", "s3"]);
	}

	[Fact]
	public async Task Where_DurationAsTimespan_Filters()
	{
		var rows = await Materialize(Exec("spans | where Duration > 200ms"));
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task Where_StatusName_Filters()
	{
		var rows = await Materialize(Exec("spans | where StatusName == 'Error'"));
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s2"]);
	}

	// --- attributes: bare-name fallback, Properties.<key>, typed conversion ---

	[Fact]
	public async Task Attributes_PropertiesPathAndBareFallback()
	{
		(await Materialize(Exec("spans | where Properties.peer == 'us'"))).Select(r => r[0])
			.Should().BeEquivalentTo(["s3"]);
		(await Materialize(Exec("spans | where peer == 'eu'"))).Select(r => r[0])
			.Should().BeEquivalentTo(["s1", "s2"]);
	}

	[Fact]
	public async Task Attributes_TypedConversion_ComparesNumerically()
	{
		var rows = await Materialize(Exec("spans | where toint(Properties.status_code) >= 500"));
		rows.Select(r => r[0]).Should().BeEquivalentTo(["s2"]);
	}

	[Fact]
	public async Task Project_AttributeValue_ViaProperties()
	{
		var result = Exec("spans | where SpanId == 's1' | project Peer = Properties.peer");
		var rows = await Materialize(result);
		rows[0][0].Should().Be("eu");
	}

	// --- a where/project/summarize/top pipeline ---

	[Fact]
	public async Task Summarize_CountByKindName()
	{
		var result = Exec("spans | summarize Cnt = count() by KindName");
		result.Columns.Select(c => c.Name).Should().ContainInOrder("KindName", "Cnt");
		var rows = await Materialize(result);
		var by = rows.ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		by["Server"].Should().Be(2);
		by["Client"].Should().Be(1);
	}

	[Fact]
	public async Task WhereProjectSummarizeTop_Pipeline()
	{
		// error+ok spans grouped by trace, keep the busiest trace.
		var result = Exec(
			"spans | where Kind == 1 or Kind == 2 | project TraceId, Duration " +
			"| summarize Total = count() by TraceId | top 1 by Total desc");
		var rows = await Materialize(result);
		rows.Should().ContainSingle();
		rows[0][Col(result, "TraceId")].Should().Be("t1"); // t1 has s1+s2, t2 has s3
		rows[0][Col(result, "Total")].Should().Be(2L);
	}

	[Fact]
	public async Task Top_ByDuration_SortsDescending()
	{
		var result = Exec("spans | project SpanId, Duration | top 2 by Duration desc");
		var rows = await Materialize(result);
		rows.Select(r => r[Col(result, "SpanId")]).Should().ContainInOrder("s2", "s1");
	}

	[Fact]
	public async Task Extend_ComputedColumn_OverSpans()
	{
		var result = Exec("spans | extend Slow = Duration > 200ms | where SpanId == 's2' | project Slow");
		var rows = await Materialize(result);
		rows[0][0].Should().Be(true);
	}

	// --- correlation op from the catalog: self-join over the SAME spans root (cross-source is out of
	// scope; same-root join is part of the catalog) ---

	[Fact]
	public async Task Join_SameSpansRoot_OnTraceId()
	{
		// pair each server span with client spans in the same trace.
		var result = Exec(
			"spans | where Kind == 1 | join kind=inner (spans | where Kind == 2) on TraceId | project SpanId, SpanId1");
		var rows = await Materialize(result);
		rows.Should().ContainSingle(); // only t1 has both a server (s1) and client (s2) span
		rows[0][Col(result, "SpanId")].Should().Be("s1");
		rows[0][Col(result, "SpanId1")].Should().Be("s2");
	}

	// --- structural-error parity: an unsupported construct over spans errors the same way as over events ---

	[Fact]
	public void UnsupportedOperator_OverSpans_ThrowsSameAsEvents()
	{
		var spanAct = () => KqlTransformer.ExecuteSpans(Src(), Parse("spans | sample 3"));
		var eventAct = () => KqlTransformer.Execute(Array.Empty<LogEntryRecord>().AsQueryable(), Parse("events | sample 3"));

		var spanEx = spanAct.Should().Throw<UnsupportedKqlException>().Which;
		var eventEx = eventAct.Should().Throw<UnsupportedKqlException>().Which;
		spanEx.Message.Should().Be(eventEx.Message); // same structural error text
	}

	[Fact]
	public void UnsupportedFunction_OverSpans_ThrowsPrecise()
	{
		var act = () => KqlTransformer.ExecuteSpans(Src(), Parse("spans | project X = strlen(Name)"));
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*strlen*not supported*");
	}

	[Fact]
	public void UnknownTable_ListsBothRoots()
	{
		var act = () => KqlTransformer.Execute(Array.Empty<LogEntryRecord>().AsQueryable(), Parse("bogus | take 1"));
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*events*spans*");
	}

	// Apply is the events-ONLY engine entry (the share surfaces); its message must not claim spans
	// support — surfaces that route both roots reject unknown roots before reaching it (spans-review 8).
	[Fact]
	public void Apply_NonEventsRoot_ClaimsEventsOnly()
	{
		var act = () => KqlTransformer.Apply(Array.Empty<LogEntryRecord>().AsQueryable(), Parse("spans | take 1")).ToList();
		act.Should().Throw<UnsupportedKqlException>().WithMessage("*only 'events'*");
	}

	// --- spans-review fix 4: literal normalization (timespan → ticks) applies on the record/SQL path,
	// so between/in over Duration agree with the post-split (TimeSpan-typed) path ---

	[Fact]
	public async Task DurationBetween_PreAndPostSplit_Agree()
	{
		var pre = await Materialize(Exec("spans | where Duration between (50ms .. 100ms) | project SpanId"));
		var post = await Materialize(Exec("spans | extend X = 1 | where Duration between (50ms .. 100ms) | project SpanId"));
		pre.Select(r => r[0]).Should().BeEquivalentTo(["s1", "s3"]);
		post.Select(r => r[0]).Should().BeEquivalentTo(pre.Select(r => r[0]));
	}

	[Fact]
	public async Task DurationIn_PreAndPostSplit_Agree()
	{
		var pre = await Materialize(Exec("spans | where Duration in (400ms) | project SpanId"));
		var post = await Materialize(Exec("spans | extend X = 1 | where Duration in (400ms) | project SpanId"));
		pre.Select(r => r[0]).Should().BeEquivalentTo(["s2"]);
		post.Select(r => r[0]).Should().BeEquivalentTo(pre.Select(r => r[0]));
	}

	// --- spans-review fix 3: case-insensitive special columns hit the right literal coercion ---

	[Fact]
	public async Task LowercaseStartAndDuration_BindToSpecialColumns()
	{
		(await Materialize(Exec("spans | where start >= datetime(2026-04-19T10:00:01Z)"))).Select(r => r[0])
			.Should().BeEquivalentTo(["s2", "s3"]);
		(await Materialize(Exec("spans | where duration > 200ms"))).Select(r => r[0])
			.Should().BeEquivalentTo(["s2"]);
	}
}
