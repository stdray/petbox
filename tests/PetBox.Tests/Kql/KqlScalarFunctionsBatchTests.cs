using Kusto.Language;

namespace PetBox.Tests.Kql;

// Coverage for the work/kql-scalar-strings-math-datetime wave: the string, math, datetime-part and
// conversion scalars added to KqlScalarFunctions.Registry.
//
// EVERY assertion runs on EVERY backend in KqlBackendConfig.Active (Sqlite AND DuckDb) through the
// PRODUCTION path (KqlTestHost seeds a real store and KqlTransformer compiles linq2db SQL). That is not
// ceremony: the engine has a SINGLE SQL path (no in-memory execution), so a shim that does not translate
// fails here at enumeration, and a shim whose two [Sql.Expression] arms disagree shows up as a per-backend
// value mismatch. The expected values are the documented Kusto examples wherever one exists (each is
// quoted next to the assertion), plus the edge cases the docs pin: null/empty input, the domain guards
// (sqrt/log/pow), negative precisions, and the timespan-typed results.
//
// The in-memory body of every shim is exercised by the SAME query: each project/extend expression is
// compiled twice — once over the storage (SQL) context for the SELECT and once over RowScalarContext for
// its LOGICAL type (KqlTransformer's "logical typing") — so a body that disagrees with its SQL translation
// in TYPE shows up here too.
public sealed class KqlScalarFunctionsBatchTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	// One deterministic row: a Thursday at midnight, a message with leading/trailing whitespace and a
	// non-ASCII letter, Level 3 (an INT column, for the tohex int arm).
	static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

	static readonly LogEntryRecord[] Rows =
	[
		new()
		{
			Id = 42,
			TimestampMs = new DateTimeOffset(Ts).ToUnixTimeMilliseconds(),
			Level = (int)LogLevel.Warning,
			Message = "  Hello, Wörld  ",
			ServiceKey = "svc",
			PropertiesJson = "{}",
		},
	];

	// One projection list on one backend: the single row's cells, in column order.
	static async Task<object?[]> Cells(string projection, KqlBackend backend)
	{
		var (_, rows) = await KqlTestHost.ExecuteAsync(
			Rows, Parse($"events | where Id == 42 | project {projection}"), backend);
		rows.Should().HaveCount(1);
		return rows[0];
	}

	// The same projection list on EVERY active backend, asserted cell-for-cell identical, returning the
	// cells of the first one — so every value test below is also a cross-backend parity test.
	static async Task<object?[]> Agreed(string projection)
	{
		object?[]? agreed = null;
		foreach (var backend in KqlBackendConfig.Active)
		{
			var cells = await Cells(projection, backend);
			if (agreed is null)
			{
				agreed = cells;
				continue;
			}
			cells.Should().BeEquivalentTo(agreed, $"`{projection}` must agree between {KqlBackendConfig.Active[0]} and {backend}");
		}
		return agreed!;
	}

	static async Task<object?> One(string expr) => (await Agreed($"V = {expr}"))[0];

	// --- string scalars ---

	[Fact]
	public async Task String_Scalars()
	{
		var c = await Agreed(
			"Len = strlen(Message), LenEmpty = strlen(''), LenAscii = strlen('abcde'), "
			+ "Idx = indexof(Message, 'Hello'), IdxMissing = indexof(Message, 'z'), "
			+ "IdxStart = indexof(Message, 'l', 5), IdxWindow = indexof(Message, 'l', 0, 4), "
			+ "IdxNegStart = indexof(Message, 'l', -6), IdxTooNegative = indexof(Message, 'l', -99), "
			+ "IdxBadLength = indexof(Message, 'l', 0, -2), "
			+ "Replaced = replace_string(Message, 'l', 'L'), ReplacedEmptyLookup = replace_string(Message, '', 'X'), "
			+ "Trimmed = trim(@'\\s+', Message), TrimStart = trim_start(@'\\s+', Message), TrimEnd = trim_end(@'\\s+', Message), "
			+ "TrimRepeated = trim('-', '--a--'), TrimDot = trim(@'[^\\w]+', '-  Te st1// $'), "
			+ "Delim = strcat_delim('-', 'a', 1, 'b'), DelimNull = strcat_delim('-', 'a', strcat('', ''), 'b'), "
			+ "Ascii = isascii('abc'), AsciiNo = isascii(Message), Utf8 = isutf8(Message), Utf8Empty = isutf8('')");

		// strlen counts Unicode CODE POINTS (kusto/query/strlen-function); the message is 16 code points.
		c[0].Should().Be(16L);
		c[1].Should().Be(0L);
		c[2].Should().Be(5L);
		// indexof is 0-BASED and -1 when absent (kusto/query/indexof-function).
		c[3].Should().Be(2L);
		c[4].Should().Be(-1L);
		c[5].Should().Be(5L);
		// length 4 examines ["  He"], which holds no 'l'.
		c[6].Should().Be(-1L);
		// A negative start offsets from the END of the source (abs(start) steps back).
		c[7].Should().Be(12L);
		c[8].Should().BeNull("a start that is still negative after the end-offset is null");
		c[9].Should().BeNull("length < -1 is null");
		// replace_string replaces ALL non-overlapping matches.
		c[10].Should().Be("  HeLLo, WörLd  ");
		c[11].Should().Be("  Hello, Wörld  ", "an empty lookup is a no-op");
		// trim/trim_start/trim_end take a REGEX and strip ALL leading/trailing matches of it.
		c[12].Should().Be("Hello, Wörld");
		c[13].Should().Be("Hello, Wörld  ");
		c[14].Should().Be("  Hello, Wörld");
		// The anchored `^(?:P)+` repetition reproduces Kusto's loop over repeated matches.
		c[15].Should().Be("a");
		c[16].Should().Be("Te st1");
		// strcat_delim joins with the first argument; a null operand renders as empty (like strcat).
		c[17].Should().Be("a-1-b");
		c[18].Should().Be("a--b");
		c[19].Should().Be(true);
		c[20].Should().Be(false);
		c[21].Should().Be(true);
		c[22].Should().Be(true);
	}

	[Fact]
	public async Task FormatBytes_Scalars()
	{
		// kusto/query/format-bytes-function documents all four of these.
		var c = await Agreed(
			"Bytes = format_bytes(564), Kb = format_bytes(10332, 1), Mb = format_bytes(20010332), "
			+ "Mb2 = format_bytes(20010332, 2), KbExplicit = format_bytes(20010332, 0, 'KB')");
		c[0].Should().Be("564 Bytes");
		c[1].Should().Be("10.1 KB");
		c[2].Should().Be("19 MB");
		c[3].Should().Be("19.08 MB");
		c[4].Should().Be("19541 KB");
	}

	[Fact]
	public async Task String_Scalars_Compose_With_Other_Scalars()
	{
		// strlen's result is an integer in the numeric domain (usable in arithmetic and comparisons).
		(await One("strlen(Message) + 1")).Should().Be(17L);
		(await One("strlen(trim(@'\\s+', Message))")).Should().Be(12L);
		(await One("toupper(replace_string(Message, 'Wörld', 'world'))")).Should().Be("  HELLO, WORLD  ");
	}

	// --- math scalars ---

	[Fact]
	public async Task Math_Scalars()
	{
		var c = await Agreed(
			"AbsInt = abs(-3), AbsReal = abs(-3.5), Floor = floor(2.7), FloorNeg = floor(-1.2), "
			+ "Ceiling = ceiling(2.1), Sqrt = sqrt(16), SqrtNeg = sqrt(-1), "
			+ "Pow = pow(2, 3), PowHalf = pow(4, 0.5), PowNegBase = pow(-8, 0.5), PowNegBaseInt = pow(-8, 2), "
			+ "Exp = exp(0), Log = log(1), LogE = log(5), LogZero = log(0), LogNeg = log(-1), "
			+ "Log2 = log2(8), Log10 = log10(100), SignNeg = sign(-42), SignZero = sign(0), SignPos = sign(11.2), "
			+ "Pi = pi(), MinOf = min_of(10, 1, -3, 17), MaxOf = max_of(10, 1, -3, 17), "
			+ "Coalesce = coalesce(tolong('not a number'), tolong('42'), 33), "
			+ "CoalesceFirst = coalesce(7, 8), NotNaN = isnan(1.5), NotInf = isinf(1.5), Inf = isinf(pow(10, 400))");

		c[0].Should().Be(3L);
		c[1].Should().Be(3.5);
		c[2].Should().Be(2.0);
		c[3].Should().Be(-2.0);
		c[4].Should().Be(3.0);
		c[5].Should().Be(4.0);
		// The domain guards: SQLite yields NULL where DuckDB RAISES for sqrt(-1)/ln(-1)/pow(-8,0.5), and NaN
		// is not representable in SQLite at all — so every domain edge is pinned to NULL on both.
		c[6].Should().BeNull();
		c[7].Should().Be(8.0);
		c[8].Should().Be(2.0);
		c[9].Should().BeNull();
		c[10].Should().Be(64.0);
		c[11].Should().Be(1.0);
		c[12].Should().Be(0.0);
		// log(5) is the natural logarithm (kusto/query/log-function: 1.6094379124341003).
		c[13].Should().Be(Math.Log(5));
		c[14].Should().BeNull("log of a non-positive number is null");
		c[15].Should().BeNull();
		c[16].Should().Be(3.0);
		c[17].Should().Be(2.0);
		c[18].Should().Be(-1L);
		c[19].Should().Be(0L);
		c[20].Should().Be(1L);
		c[21].Should().Be(Math.PI);
		c[22].Should().Be(-3L);
		c[23].Should().Be(17L);
		// coalesce skips a null conversion result; a non-nullable first argument wins at compile time.
		c[24].Should().Be(42L);
		c[25].Should().Be(7L);
		c[26].Should().Be(false);
		c[27].Should().Be(false);
		c[28].Should().Be(true);
	}

	[Fact]
	public async Task Round_MatchesKustoHalfAwayFromZero()
	{
		// Kusto rounds to N digits, HALF AWAY FROM ZERO, with a NEGATIVE precision allowed
		// (kusto/query/round-function documents each of these five outputs).
		var c = await Agreed(
			"R1 = round(2.98765, 3), R2 = round(2.15, 1), R3 = round(2.15), R4 = round(-50.55, -2), "
			+ "R5 = round(21.5, -1), Half = round(2.5), NegHalf = round(-2.5)");
		c[0].Should().Be(2.988);
		c[1].Should().Be(2.2);
		c[2].Should().Be(2.0);
		c[3].Should().Be(-100.0);
		c[4].Should().Be(20.0);
		c[5].Should().Be(3.0);
		c[6].Should().Be(-3.0);
	}

	[Fact]
	public async Task Math_NullableOperands_PropagateNull()
	{
		// Every math scalar is TOTAL: a null operand (a malformed/missing bag value through toint/todouble)
		// yields null instead of failing the query.
		var c = await Agreed(
			"AbsN = abs(tolong('nope')), SqrtN = sqrt(tolong('nope')), PowN = pow(tolong('nope'), 2), "
			+ "RoundN = round(todouble('nope'), 2), SignN = sign(tolong('nope'))");
		foreach (var cell in c)
			cell.Should().BeNull();
	}

	[Fact]
	public async Task MinOf_MaxOf_OverStringsAndNullableNumerics()
	{
		// Non-null values take precedence over null (kusto/query/max-of-function), for strings and numbers
		// alike; a null string arrives through tostring() of a null conversion.
		var c = await Agreed(
			"StrMax = max_of('a', 'c'), StrMin = min_of('a', 'c'), StrNullMax = max_of(tostring(tolong('x')), 'x'), "
			+ "StrNullMin = min_of('x', tostring(tolong('x'))), NumNullMax = max_of(tolong('x'), 5), "
			+ "NumNullMin = min_of(tolong('x'), 5), BothNull = max_of(tolong('x'), todouble('y'))");
		c[0].Should().Be("c");
		c[1].Should().Be("a");
		c[2].Should().Be("x");
		c[3].Should().Be("x");
		c[4].Should().Be(5L);
		c[5].Should().Be(5L);
		c[6].Should().BeNull("all-null arguments stay null");
	}

	// --- datetime parts / arithmetic ---

	[Fact]
	public async Task DateTimePart_Scalars()
	{
		// 2026-01-01T00:00Z is a THURSDAY, and the first ISO week of 2026.
		var c = await Agreed(
			"Dow = dayofweek(Timestamp), Dom = dayofmonth(Timestamp), Doy = dayofyear(Timestamp), "
			+ "Month = monthofyear(Timestamp), Hour = hourofday(Timestamp), Week = week_of_year(Timestamp), "
			+ "PartYear = datetime_part('year', Timestamp), PartQuarter = datetime_part('quarter', Timestamp), "
			+ "PartDow = datetime_part('dayofyear', Timestamp), PartMs = datetime_part('millisecond', Timestamp)");

		// dayofweek is a TIMESPAN — the number of days since the preceding Sunday (kusto/query/day-of-week-function).
		c[0].Should().Be(TimeSpan.FromDays(4));
		c[1].Should().Be(1L);
		c[2].Should().Be(1L);
		c[3].Should().Be(1L);
		c[4].Should().Be(0L);
		c[5].Should().Be(1L);
		c[6].Should().Be(2026L);
		c[7].Should().Be(1L);
		c[8].Should().Be(1L);
		c[9].Should().Be(0L);
	}

	[Fact]
	public async Task WeekOfYear_IsIso8601_AcrossYearBoundaries()
	{
		// The ISO-8601 week rule (first week = the one with the first Thursday) is the one place SQLite has
		// no native function, so the SQLite arm computes it from strftime while DuckDB uses week(): these
		// five dates are exactly where a divergence would hide (kusto/query/week-of-year-function).
		var c = await Agreed(
			"W1 = week_of_year(make_datetime(2020, 12, 31)), W2 = week_of_year(make_datetime(2000, 1, 1)), "
			+ "W3 = week_of_year(make_datetime(1970, 1, 1)), W4 = week_of_year(make_datetime(2020, 6, 15)), "
			+ "W5 = week_of_year(make_datetime(2021, 1, 3))");
		c[0].Should().Be(53L);
		c[1].Should().Be(52L);
		c[2].Should().Be(1L);
		c[3].Should().Be(25L);
		c[4].Should().Be(53L, "2021-01-03 (Sunday) still belongs to ISO week 53 of 2020");
	}

	[Fact]
	public async Task DateTimeAdd_And_EndOf_Periods()
	{
		var c = await Agreed(
			"AddDay = datetime_add('day', 1, Timestamp), AddWeek = datetime_add('week', 1, Timestamp), "
			+ "AddHour = datetime_add('hour', -2, Timestamp), AddMs = datetime_add('millisecond', 500, Timestamp), "
			+ "Eod = endofday(Timestamp), EodPrev = endofday(Timestamp, -1), "
			+ "Eow = endofweek(Timestamp), Eom = endofmonth(Timestamp), EomPrev = endofmonth(Timestamp, -1)");
		c[0].Should().Be(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
		c[1].Should().Be(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc));
		c[2].Should().Be(new DateTime(2025, 12, 31, 22, 0, 0, DateTimeKind.Utc));
		c[3].Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, 500, DateTimeKind.Utc));
		// endof* is the LAST instant of the period at the engine's MILLISECOND resolution (Kusto's
		// ...9999999 truncated), and the week ends on SATURDAY.
		c[4].Should().Be(new DateTime(2026, 1, 1, 23, 59, 59, 999, DateTimeKind.Utc));
		c[5].Should().Be(new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc));
		c[6].Should().Be(new DateTime(2026, 1, 3, 23, 59, 59, 999, DateTimeKind.Utc));
		c[7].Should().Be(new DateTime(2026, 1, 31, 23, 59, 59, 999, DateTimeKind.Utc));
		c[8].Should().Be(new DateTime(2025, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc));
	}

	[Fact]
	public async Task MakeDateTime_And_MakeTimespan()
	{
		var c = await Agreed(
			"Ymd = make_datetime(2017, 10, 1), YmdHm = make_datetime(2017, 10, 1, 12, 10), "
			+ "YmdHms = make_datetime(2017, 10, 1, 12, 11, 0.123), Leap = make_datetime(2024, 2, 29), "
			+ "Ts4 = make_timespan(1, 12, 30, 55.123), Ts2 = make_timespan(1, 30), Ts3 = make_timespan(0, 3, 0), "
			+ "TsBadHour = make_timespan(25, 0), TsBadSecond = make_timespan(0, 0, 60)");
		c[0].Should().Be(new DateTime(2017, 10, 1, 0, 0, 0, DateTimeKind.Utc));
		c[1].Should().Be(new DateTime(2017, 10, 1, 12, 10, 0, DateTimeKind.Utc));
		c[2].Should().Be(new DateTime(2017, 10, 1, 12, 11, 0, DateTimeKind.Utc).AddMilliseconds(123));
		c[3].Should().Be(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc));
		c[4].Should().Be(new TimeSpan(1, 12, 30, 55, 123));
		c[5].Should().Be(TimeSpan.FromMinutes(90));
		c[6].Should().Be(TimeSpan.FromMinutes(3));
		// Out-of-range components yield NULL (kusto/query/make-timespan-function).
		c[7].Should().BeNull();
		c[8].Should().BeNull();
	}

	// --- conversion scalars ---

	[Fact]
	public async Task Conversion_Scalars()
	{
		var c = await Agreed(
			"BoolTrue = toboolean('true'), BoolFalse = toboolean('false'), BoolOne = toboolean('1'), "
			+ "BoolBad = toboolean('nope'), HexPlain = tohex(256), HexNeg = tohex(-256), "
			+ "HexPadded = tohex(256, 8), HexShortPad = tohex(256, 2), HexInt = tohex(Level), "
			+ "HexZero = tohex(0)");
		c[0].Should().Be(true);
		c[1].Should().Be(false);
		c[2].Should().Be(true);
		c[3].Should().BeNull();
		// kusto/query/tohex-function pins the lowercase, two's-complement rendering.
		c[4].Should().Be("100");
		c[5].Should().Be("ffffffffffffff00");
		c[6].Should().Be("00000100");
		c[7].Should().Be("100", "minLength is a MINIMUM: a longer rendering is never truncated");
		c[8].Should().Be("3");
		c[9].Should().Be("0");
	}

	// --- the `where` position: the SAME shims must translate in a pre-split predicate too ---

	[Fact]
	public void Scalars_InWhere_TranslateOnEveryBackend()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			KqlTestHost.Apply(Rows, Parse("events | where strlen(Message) == 16"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where indexof(Message, 'Hello') == 2"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where trim(@'\\s+', Message) == 'Hello, Wörld'"), backend)
				.Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where abs(Level - 5) == 2"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where dayofmonth(Timestamp) == 1"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where hourofday(Timestamp) == 0"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where week_of_year(Timestamp) == 1"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where endofmonth(Timestamp) > Timestamp"), backend)
				.Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where tohex(Level) == '3'"), backend).Should().ContainSingle();
			KqlTestHost.Apply(Rows, Parse("events | where isascii('abc')"), backend).Should().ContainSingle();
			// A false predicate on the same shim excludes the row (proves the SQL is a real predicate, not a
			// constant-folded true).
			KqlTestHost.Apply(Rows, Parse("events | where strlen(Message) == 17"), backend).Should().BeEmpty();
			KqlTestHost.Apply(Rows, Parse("events | where sqrt(-1) == 0"), backend).Should().BeEmpty();
		}
	}

	[Fact]
	public async Task Scalars_AsGroupingKeys_Translate()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (columns, rows) = await KqlTestHost.ExecuteAsync(Rows,
				Parse("events | summarize N = count() by H = hourofday(Timestamp), D = trim(@'\\s+', Message)"), backend);
			columns.Select(c => c.Name).Should().Contain("H");
			columns.Select(c => c.Name).Should().Contain("D");
			rows.Should().ContainSingle();
			rows[0][0].Should().Be(0L);
			rows[0][1].Should().Be("Hello, Wörld");
			rows[0][2].Should().Be(1L);
		}
	}

	// --- precise refusals for what the single SQL path cannot express ---

	[Theory]
	[InlineData("indexof(Message, 'l', 0, -1, 2)", "*indexof*occurrence*")]
	[InlineData("trim(Message, Message)", "*trim*LITERAL*")]
	[InlineData("datetime_add('month', 1, Timestamp)", "*datetime_add*month*")]
	[InlineData("datetime_part('nanosecond', Timestamp)", "*datetime_part*nanosecond*")]
	[InlineData("endofmonth(Timestamp, 1)", "*endofmonth*offsets 0 and -1*")]
	[InlineData("make_datetime(2024, 2, 31)", "*make_datetime*not a valid*")]
	[InlineData("format_bytes(1, 2, 'XB')", "*format_bytes*unit*")]
	public async Task Unsupported_Shapes_AreRefusedPrecisely(string expr, string message)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var act = async () => await KqlTestHost.ExecuteAsync(Rows, Parse($"events | project V = {expr}"), backend);
			(await act.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage(message);
		}
	}

	[Fact]
	public async Task Arity_And_Type_Errors_NameTheFunction()
	{
		(await ((Func<Task>)(() => KqlTestHost.ExecuteAsync(Rows, Parse("events | project V = strlen()"), KqlBackend.Sqlite)))
			.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage("*strlen*1 argument*");

		(await ((Func<Task>)(() => KqlTestHost.ExecuteAsync(Rows, Parse("events | project V = abs(Message)"), KqlBackend.Sqlite)))
			.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage("*abs*numeric*");

		(await ((Func<Task>)(() => KqlTestHost.ExecuteAsync(Rows, Parse("events | project V = pi(1)"), KqlBackend.Sqlite)))
			.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage("*pi*no arguments*");
	}
}
