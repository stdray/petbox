using Kusto.Language;
using PetBox.Log.Core.Query;

namespace PetBox.Tests.Kql;

// The two COMPOSER gaps a `summarize` used to have, pinned on BOTH backends (SQLite == DuckDb value-exact,
// plus the hand-computed expectations):
//
//  GAP 1 — a `summarize` WITHOUT `by` (and the `count` operator) was a TERMINAL stage: any following operator
//    refused with "operator 'ProjectOperator' is not supported in this position". Root cause: the constant-group
//    shape was materialized as a finished KqlResult instead of composing, so it could not feed ComposeLoop. It is
//    now a one-row SqlStage — the Kusto empty-input default row is built INTO the query — so the later stages
//    thread over that row like over any other stage, including the default row itself
//    (`… | summarize count() | project count_` over an EMPTY table is ONE row of 0, not zero rows).
//
//  GAP 2 — a SCALAR OVER AN AGGREGATE (`round(avg(Level), 1)`) used to report "aggregate 'round' not supported":
//    the aggregate had to be the ROOT of the summarize element. The aggregate is now found inside the scalar tree
//    and the scalar compiled over its group-level SQL expression (ROUND(AVG(…), 1)), with no per-function twin.
//    The unknown-aggregate error is unchanged and still names the supported list.
public sealed class KqlSummarizeCompositionTests
{
	static LogEntryRecord Rec(long id, int level = 2, string message = "m", string serviceKey = "s") => new()
	{
		Id = id,
		Level = level,
		Message = message,
		MessageTemplate = message,
		ServiceKey = serviceKey,
		PropertiesJson = "{}",
		TimestampMs = id,
	};

	// Runs the query on BOTH backends, asserts value-exact cross-backend equality (the differential pin),
	// returns the SQLite columns + rows.
	static async Task<(IReadOnlyList<string> Columns, List<object?[]> Rows)> RunBoth(
		IReadOnlyList<LogEntryRecord> data, string kql)
	{
		var code = KustoCode.Parse(kql);
		var (sqliteCols, sqlite) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);
		var (duckCols, duck) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb);
		duck.Should().BeEquivalentTo(sqlite, o => o.WithStrictOrdering(),
			$"both backends must agree value-exactly on {kql}");
		duckCols.Select(c => c.Name).Should().Equal(sqliteCols.Select(c => c.Name),
			"the output schema is backend-independent");
		return (sqliteCols.Select(c => c.Name).ToList(), sqlite);
	}

	static readonly IReadOnlyList<LogEntryRecord> Data =
	[
		Rec(1, 1, "a", "a"),
		Rec(2, 3, "b", "a"),
		Rec(3, 6, "c", "a"),
		Rec(4, 4, "d", "b"),
	];

	// ---- GAP 1: a following operator composes over the single-group row ----

	// The canonical shape from the card: `summarize count() | project count_`.
	[Fact]
	public async Task SummarizeWithoutBy_ComposesIntoProject()
	{
		var (cols, rows) = await RunBoth(Data, "events | summarize count() | project count_");
		cols.Should().Equal("count_");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(4L);
	}

	// Same for the `count` OPERATOR, which is the other one-row terminal: `count | project Count`.
	[Fact]
	public async Task CountOperator_ComposesIntoProject()
	{
		var (cols, rows) = await RunBoth(Data, "events | count | project Count");
		cols.Should().Equal("Count");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(4L);
	}

	// `extend` over the aggregate row (the card's second refusal): the computed column is evaluated over the
	// one summarized row.
	[Fact]
	public async Task SummarizeWithoutBy_ComposesIntoExtend()
	{
		var (cols, rows) = await RunBoth(Data, "events | summarize max(Level) | extend twice = max_ * 2");
		cols.Should().Equal("max_", "twice");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(6, 12L);
	}

	// `arg_max` over the group, then the natural follow-up `project` — the shape the aggregate wave unlocked.
	[Fact]
	public async Task ArgMax_WithoutBy_ComposesIntoProject()
	{
		var (cols, rows) = await RunBoth(Data, "events | summarize arg_max(Level, Message) | project Message");
		cols.Should().Equal("Message");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal("c");
	}

	// where / order / take over the one-row stage: a filter on the aggregate VALUE, which is why the row has to
	// flow through the downstream stages rather than being appended at materialization.
	[Fact]
	public async Task SummarizeWithoutBy_ComposesIntoWhereOrderTake()
	{
		var (_, filtered) = await RunBoth(Data, "events | summarize count() | where count_ > 3 | order by count_ asc | take 1");
		filtered.Should().HaveCount(1);
		filtered[0].Should().Equal(4L);

		var (_, none) = await RunBoth(Data, "events | summarize count() | where count_ > 99");
		none.Should().BeEmpty("the filter runs over the summarized row, so it can drop it");
	}

	// A `by`-grouped summarize is untouched by all of this — the control that the constant-group fix did not
	// disturb the normal grouped composition.
	[Fact]
	public async Task SummarizeWithBy_StillComposes()
	{
		var (cols, rows) = await RunBoth(Data,
			"events | summarize count() by ServiceKey | project ServiceKey, count_ | order by ServiceKey asc");
		cols.Should().Equal("ServiceKey", "count_");
		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 3L);
		rows[1].Should().Equal("b", 1L);
	}

	// Bare terminals still work exactly as before (no following operator).
	[Fact]
	public async Task BareTerminals_Unchanged()
	{
		var (cols, rows) = await RunBoth(Data, "events | summarize count()");
		cols.Should().Equal("count_");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(4L);

		var (countCols, countRows) = await RunBoth(Data, "events | count");
		countCols.Should().Equal("Count");
		countRows.Should().HaveCount(1);
		countRows[0].Should().Equal(4L);
	}

	// ---- GAP 1: the EMPTY-INPUT default row must flow THROUGH the later stages ----

	// Kusto's one-default-row rule, end to end: over an empty table `summarize count() | project count_` is ONE
	// row of 0 — not zero rows, and not a row synthesized after the project.
	[Fact]
	public async Task EmptyInput_SummarizeThenProject_YieldsOneRowOfZero()
	{
		var (cols, rows) = await RunBoth([], "events | summarize count() | project count_");
		cols.Should().Equal("count_");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(0L);
	}

	// The default row is data by the time the later stages see it: an extend computes over it (0 * 2 = 0).
	[Fact]
	public async Task EmptyInput_SummarizeThenExtend_ComputesOverTheDefaultRow()
	{
		var (cols, rows) = await RunBoth([], "events | summarize count() | extend twice = count_ * 2");
		cols.Should().Equal("count_", "twice");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(0L, 0L);
	}

	// sum/avg/min/max default to NULL over empty input (a NULL-typed row, not a missing one), through a project.
	[Fact]
	public async Task EmptyInput_NullableAggregates_DefaultToNull()
	{
		var (_, rows) = await RunBoth([], "events | summarize max(Level), avg(Level) | project max_, avg_");
		rows.Should().HaveCount(1);
		rows[0].Should().HaveCount(2);
		rows[0][0].Should().BeNull();
		rows[0][1].Should().BeNull();
	}

	// make_list is the aggregate whose SQL renders as a CORRELATED SUBQUERY (one identical row per input row,
	// ZERO rows over empty input): it is the case the one-row cap and the union'd default row exist for. Kusto
	// yields the EMPTY array, so the downstream project sees "[]".
	[Fact]
	public async Task EmptyInput_MakeList_DefaultsToEmptyArray_ThroughProject()
	{
		var (_, rows) = await RunBoth([], "events | summarize make_list(Message) | project make_list_");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal("[]");
	}

	// ... and over NON-empty input it stays exactly one row (the correlated form would otherwise emit one row
	// per input row).
	[Fact]
	public async Task NonEmptyInput_MakeList_YieldsExactlyOneRow()
	{
		var (_, rows) = await RunBoth(Data, "events | summarize make_list(Message) | project make_list_");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal("[\"a\",\"b\",\"c\",\"d\"]");
	}

	// ---- GAP 2: a scalar over an aggregate ----

	// The card's second acceptance shape, verbatim.
	[Fact]
	public async Task ScalarOverAggregate_GroupedRoundOfAvg()
	{
		var (cols, rows) = await RunBoth(Data,
			"events | summarize round(avg(Level), 1) by ServiceKey | order by ServiceKey asc");
		cols.Should().Equal("ServiceKey", "round_");
		// a: (1+3+6)/3 = 3.333… → 3.3 ; b: 4
		rows.Should().HaveCount(2);
		rows[0].Should().Equal("a", 3.3);
		rows[1].Should().Equal("b", 4.0);
	}

	// The no-`by` form (both gaps at once), aliased: the aggregate is found inside the scalar tree and the whole
	// element becomes one column of the single summarized row.
	[Fact]
	public async Task ScalarOverAggregate_WithoutBy_Aliased()
	{
		var (cols, rows) = await RunBoth(Data, "events | summarize r = round(avg(Level), 1) | project r");
		cols.Should().Equal("r");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(3.5);
	}

	// A scalar wrapping a COUNT (integer aggregate, text-typed scalar result) — the substitution is not numeric-only.
	[Fact]
	public async Task ScalarOverAggregate_TextResult()
	{
		var (cols, rows) = await RunBoth(Data, "events | summarize s = tostring(count()) | project s");
		cols.Should().Equal("s");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal("4");
	}

	// Nested scalar wrappers resolve one aggregate and wrap it twice.
	[Fact]
	public async Task ScalarOverAggregate_NestedScalars()
	{
		var (_, rows) = await RunBoth(Data, "events | summarize r = round(round(avg(Level), 1), 0) | project r");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(4.0);
	}

	// A percentile inside the scalar tree is still STAGED by the window pre-stage (SQLite has no native
	// quantile), so the nested call must be discovered like a root one — the nearest-rank pick and DuckDb's
	// native quantile_disc agree on the value.
	[Fact]
	public async Task ScalarOverAggregate_NestedPercentile()
	{
		var (_, rows) = await RunBoth(Data, "events | summarize r = round(percentile(Level, 50), 0) | project r");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(3.0);
	}

	// An UNKNOWN aggregate keeps the supported-list error — the message gap 2 was about stays for the genuine case.
	[Fact]
	public void UnknownAggregate_StillNamesTheSupportedList()
	{
		var act = () => KqlTransformer.Execute(
			new[] { Rec(1) }.AsQueryable(), KustoCode.Parse("events | summarize bogus(Level)"));

		act.Should().Throw<UnsupportedKqlException>()
			.WithMessage("*aggregate 'bogus' not supported (supported: arg_max, arg_min, avg, *");
	}

	// A scalar around NO aggregate is still an unknown aggregate (the element must compute something groupable),
	// and an aggregate that cannot be wrapped (arg_max returns one column per argument) says so precisely.
	[Fact]
	public void ScalarWithoutAggregate_And_UnwrappableAggregate_Error()
	{
		var noAgg = () => KqlTransformer.Execute(
			new[] { Rec(1) }.AsQueryable(), KustoCode.Parse("events | summarize round(Level, 1)"));
		noAgg.Should().Throw<UnsupportedKqlException>().WithMessage("*aggregate 'round' not supported*");

		var argMax = () => KqlTransformer.Execute(
			new[] { Rec(1) }.AsQueryable(), KustoCode.Parse("events | summarize tostring(arg_max(Level, Message))"));
		argMax.Should().Throw<UnsupportedKqlException>()
			.WithMessage("*arg_max() cannot be wrapped in a scalar expression*");
	}

	// ---- P1-1: the ToSqlQuery→FromSql seam must never read USER TEXT as a composite-format string ----

	// Braces in a log message are ordinary data (JSON fragments, template placeholders). The seam that re-roots a
	// composed subquery as raw SQL parses .NET format rules, so an unescaped `{OrderId}` threw FormatException and
	// an unescaped `{0}` silently bound that parameter — turning a match into no match. Every value below is run
	// through the no-`by` summarize (the seam this branch added) on BOTH backends.
	[Theory]
	[InlineData("a { b")]
	[InlineData("a } b")]
	[InlineData("{{lit}}")]
	[InlineData("Order {OrderId} done")]
	[InlineData("x {0} y")]
	public async Task BracesInAMessage_SurviveTheNoBySeam(string message)
	{
		var data = new[] { Rec(1, 2, message), Rec(2, 2, "plain") };
		var (_, rows) = await RunBoth(data, $"events | where Message == '{message}' | summarize make_list(Message)");
		rows.Should().HaveCount(1);
		rows[0][0].Should().Be("[\"" + message + "\"]",
			$"the summarize must see the row the where matched ({message})");
	}

	// The seam renders values INLINE (no parameter, hence no composite-format pass — see InlineParameters), so a
	// value that is SQL-quote soup must survive too: a brace AND a doubled quote in one message.
	[Fact]
	public async Task QuotesAndBracesInAMessage_SurviveTheNoBySeam()
	{
		var data = new[] { Rec(1, 2, "O'Brien {x}"), Rec(2, 2, "plain") };
		var (_, rows) = await RunBoth(data, "events | where Message == \"O'Brien {x}\" | summarize make_list(Message)");
		rows.Should().HaveCount(1);
		rows[0][0].Should().Be("[\"O'Brien {x}\"]");
	}

	// The same text through the WINDOW seam (arg_max's row pick re-roots the pre-stage the same way).
	[Fact]
	public async Task BracesInAMessage_SurviveTheWindowSeam()
	{
		var data = new[] { Rec(1, 1, "a { b"), Rec(2, 9, "Order {OrderId} done") };
		var (_, rows) = await RunBoth(data,
			"events | where Message == 'Order {OrderId} done' | summarize arg_max(Level, Message) | project Message");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal("Order {OrderId} done");
	}

	// ... and through the mv-expand seam (same helper, same renderer).
	[Fact]
	public async Task BracesInAMessage_SurviveTheMvExpandSeam()
	{
		var data = new[]
		{
			new LogEntryRecord
			{
				Id = 1, Level = 2, Message = "a { b", MessageTemplate = "a { b", ServiceKey = "s",
				PropertiesJson = """{"Tags":["t1"]}""", TimestampMs = 1,
			},
		};
		var (_, rows) = await RunBoth(data, "events | where Message == 'a { b' | mv-expand Properties.Tags | project Id");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(1L);
	}

	// ---- P1-2: the composed count stage stays ONE ROW under row-CONSUMING follow-ups ----

	// A filter over the count row must see the row (`where Count > 0` came back EMPTY over non-empty input).
	[Fact]
	public async Task Count_ComposesIntoWhere()
	{
		var (cols, rows) = await RunBoth(Data, "events | count | where Count > 0");
		cols.Should().Equal("Count");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(4L);

		var (_, empty) = await RunBoth([], "events | count | where Count > 0");
		empty.Should().BeEmpty("over empty input the count row is 0, which the filter drops");
	}

	// A second `count` must count the ONE row it receives (it counted the underlying table: 4).
	[Fact]
	public async Task Count_ComposesIntoCount()
	{
		var (cols, rows) = await RunBoth(Data, "events | count | count");
		cols.Should().Equal("Count");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(1L);

		var (_, empty) = await RunBoth([], "events | count | count");
		empty.Should().HaveCount(1);
		empty[0][0].Should().Be(1L, "`count` over an empty table is one row of 0, so counting it yields 1");
	}

	// `summarize count()` over the count row: one row in, one row of 1 out.
	[Fact]
	public async Task Count_ComposesIntoSummarize()
	{
		var (_, rows) = await RunBoth(Data, "events | count | summarize count()");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(1L);

		var (_, empty) = await RunBoth([], "events | count | summarize count()");
		empty.Should().HaveCount(1);
		empty[0].Should().Equal(1L);
	}

	// project does not consume rows, so it keeps the count and the following `count` still counts ONE row.
	[Fact]
	public async Task Count_ThenProject_ThenCount()
	{
		var (_, rows) = await RunBoth(Data, "events | count | project Count | count");
		rows.Should().HaveCount(1);
		rows[0].Should().Equal(1L);

		var (_, empty) = await RunBoth([], "events | count | project Count | count");
		empty.Should().HaveCount(1);
		empty[0].Should().Equal(1L);
	}
}
