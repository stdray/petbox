using System.Text.Json;
using Kusto.Language;
using PetBox.Log.Core.Query;

namespace PetBox.Tests.Kql;

// Production-only coverage for typed Properties access: the conversion functions (tostring / toint /
// tolong / todouble / tobool / todatetime), the bare-name → Properties fallback, and null-on-malformed
// semantics. These are pinned against the production engine directly:
//   * the bare-name fallback has no KustoLoco analogue (KustoLoco resolves against a real schema and
//     rejects unknown names), so it can't be differential;
//   * todatetime / tobool and the exact null-representation live here because KustoLoco tz-shifts
//     datetime literals and models dynamic differently. `Properties.<key>` + toint/todouble/tostring
//     ARE differential (see DualExecutorTests) where KustoLoco cooperates.
public sealed class KqlTypedPropertiesTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly DateTime Ts = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static LogEntryRecord Rec(long id, string props, int level = (int)LogLevel.Information, string msg = "m", string svc = "svc") => new()
	{
		Id = id,
		ServiceKey = svc,
		TimestampMs = new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds(),
		Level = level,
		Message = msg,
		MessageTemplate = msg,
		PropertiesJson = props,
	};

	// The one production run seam: seed `data` into a fresh in-memory LogDb and Apply/Execute `kql` over the
	// real linq2db IQueryable. Sqlite is the only Active backend today.
	static IReadOnlyList<long> Ids(string kql, IReadOnlyList<LogEntryRecord> data) =>
		KqlTestHost.Apply(data, Parse(kql), KqlBackend.Sqlite).Select(r => r.Id).ToList();

	static async Task<List<object?[]>> Table(string kql, IReadOnlyList<LogEntryRecord> data)
	{
		var (_, rows) = await KqlTestHost.ExecuteAsync(data, Parse(kql), KqlBackend.Sqlite);
		return rows;
	}

	// ---- CLEF stored-shape: nails down EXACTLY what the real ingest parser writes into PropertiesJson
	// and what json_extract then returns, so the conversions above rest on a verified contract. ----

	[Fact]
	public void CleF_StoresPropertyValuesWithNativeJsonTypes_AndJsonExtractUnwraps()
	{
		// A real CLEF line: string, integer, real, and boolean properties.
		var line = """
			{"@t":"2026-04-19T10:00:00Z","@m":"x","DeviceId":"smoke-device","Status":500,"Ratio":1.5,"Flag":true}
			""";
		var parsed = CleFParser.ParseLine(line, 1);
		parsed.IsSuccess.Should().BeTrue();
		var json = parsed.Event!.Properties;

		// Values are stored with their NATIVE JSON type — NOT re-encoded as JSON-in-a-string. So a
		// string property is a JSON string, a number a JSON number, a bool a JSON bool. (A doubly-quoted
		// value like "\"smoke-device\"" would only appear if the source log itself sent a JSON-encoded
		// string; ingest passes JsonElement values through verbatim and never re-encodes them.)
		using var doc = JsonDocument.Parse(json);
		doc.RootElement.GetProperty("DeviceId").ValueKind.Should().Be(JsonValueKind.String);
		doc.RootElement.GetProperty("Status").ValueKind.Should().Be(JsonValueKind.Number);
		doc.RootElement.GetProperty("Ratio").ValueKind.Should().Be(JsonValueKind.Number);
		doc.RootElement.GetProperty("Flag").ValueKind.Should().Be(JsonValueKind.True);

		// json_extract returns a JSON string's UNQUOTED text, and a number/bool's literal text. Both the
		// SQLite translation and this in-memory body agree; the conversion functions build on this.
		KqlSqlExpressions.JsonExtract(json, "$.DeviceId").Should().Be("smoke-device");
		KqlSqlExpressions.JsonExtract(json, "$.Status").Should().Be("500");
		KqlSqlExpressions.JsonExtract(json, "$.Ratio").Should().Be("1.5");
		KqlSqlExpressions.JsonExtract(json, "$.Flag").Should().Be("true");
		KqlSqlExpressions.JsonExtract(json, "$.Missing").Should().BeNull();
	}

	// ---- typed conversions over Properties ----

	static IReadOnlyList<LogEntryRecord> ConvData =>
	[
		Rec(1, """{"Status":"200","Ratio":"1.5","Flag":"true","user":"alice"}"""),
		Rec(2, """{"Status":"500","Ratio":"2.5","Flag":"false","user":"bob"}"""),
		Rec(3, """{"Status":"nope","user":"carol"}"""), // malformed / missing → null
	];

	[Fact]
	public void Toint_OnProperty_ComparesNumerically_NotTextually()
	{
		// '500' >= 500 numerically; the malformed row yields null → excluded. A textual compare would be
		// wrong (e.g. '500' < '99' as strings).
		Ids("events | where toint(Properties.Status) >= 500", ConvData).Should().BeEquivalentTo([2L]);
		Ids("events | where toint(Properties.Status) < 300", ConvData).Should().BeEquivalentTo([1L]);
	}

	[Fact]
	public async Task Conversions_ProjectTypedValues()
	{
		var rows = await Table(
			"events | project Id, S = toint(Properties.Status), R = todouble(Properties.Ratio), B = tobool(Properties.Flag), U = tostring(Properties.user)",
			ConvData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r);
		by[1][1].Should().Be(200L);
		by[1][2].Should().Be(1.5);
		by[1][3].Should().Be(true);
		by[1][4].Should().Be("alice");
		by[2][3].Should().Be(false);
		// malformed / missing → null across every conversion
		by[3][1].Should().BeNull();
		by[3][2].Should().BeNull();
		by[3][3].Should().BeNull();
	}

	[Fact]
	public async Task Todatetime_ParsesIso8601_AndNullOnMalformed()
	{
		var data = new[]
		{
			Rec(1, """{"when":"2026-04-19T12:00:00Z"}"""),
			Rec(2, """{"when":"not-a-date"}"""),
		};
		var rows = await Table("events | project Id, W = todatetime(Properties.when)", data);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r[1]);
		by[1].Should().Be(new DateTime(2026, 4, 19, 12, 0, 0, DateTimeKind.Utc));
		by[2].Should().BeNull();

		// and honest datetime comparison in a where
		Ids("events | where todatetime(Properties.when) > datetime(2026-04-19T11:00:00Z)", data)
			.Should().BeEquivalentTo([1L]);
	}

	// CONTRACT DEVIATION (portable-parity): the typed conversions now run as native per-dialect SQL, NOT
	// .NET's culture-aware TryParse. .NET-specific spellings that CAST/unixepoch do not accept therefore
	// become null. These pins nail the TRUE post-swap behavior so the parity contract is explicit.
	[Fact]
	public async Task Conversions_DropDotNetSpecificForms_ToNull()
	{
		var data = new[]
		{
			Rec(1, """{"thou":"1,000","us":"01/02/2024","mon":"Jan 2, 2024"}"""),
		};
		var rows = await Table(
			"events | project Id, D = todouble(Properties.thou), U = todatetime(Properties.us), M = todatetime(Properties.mon)",
			data);
		// todouble('1,000'): the thousands separator is NOT accepted by the SQLite well-formedness regex
		// (was 1000 under .NET NumberStyles.AllowThousands) → null.
		rows[0][1].Should().BeNull();
		// todatetime('01/02/2024') and a month-name form: neither is ISO-8601, so SQLite unixepoch returns
		// NULL (both were parsed by .NET DateTime.TryParse before) → null.
		rows[0][2].Should().BeNull();
		rows[0][3].Should().BeNull();
	}

	// todatetime well-formedness GATE (F1): SQLite's unixepoch does NOT reject non-ISO input — it silently
	// returns a wrong non-null instant for many malformed strings. A sqlean regexp_like ISO-8601-subset gate
	// now precedes unixepoch, so non-ISO spellings become null instead of garbage. These pin the ACTUAL
	// gated behavior; the well-formed cases prove the gate didn't break valid parsing.
	static long EpochMs(object? v) => new DateTimeOffset((DateTime)v!, TimeSpan.Zero).ToUnixTimeMilliseconds();

	[Fact]
	public async Task Todatetime_GatesToIso8601Subset_RejectsNonIsoToNull()
	{
		var data = new[]
		{
			Rec(1, """{"v":"now"}"""),
			Rec(2, """{"v":"12345"}"""),
			Rec(3, """{"v":"20240102"}"""),
			Rec(4, """{"v":"12:34:56"}"""),
			Rec(5, """{"v":"2024-01-02T03:04:05Z"}"""),
			Rec(6, """{"v":"2024-01-02"}"""),
			Rec(7, """{"v":"2024-01-02T03:04:05.6789Z"}"""),
		};
		var rows = await Table("events | project Id, W = todatetime(Properties.v)", data);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r[1]);

		// non-ISO forms the sqlean regexp_like gate REJECTS → null (previously silent garbage instants)
		by[1].Should().BeNull(); // 'now'      — bare keyword
		by[2].Should().BeNull(); // '12345'    — bare number
		by[3].Should().BeNull(); // '20240102' — compact date, no separators
		by[4].Should().BeNull(); // '12:34:56' — time-only

		// well-formed ISO still parses to the correct UTC instant (epoch-ms in the SQL layer)
		EpochMs(by[5]).Should().Be(1704164645000L); // 2024-01-02T03:04:05Z
		EpochMs(by[6]).Should().Be(1704153600000L); // date-only = UTC midnight
													// >3 fractional digits: SQLite's unixepoch(...,'subsec') ROUNDS to millisecond precision internally
													// (.6789 s → .679 s) BEFORE the *1000/CAST, so the exposed value is already ms-rounded; the CAST
													// (truncate) only guards float representation drift below the ms. Observed: 679.
		EpochMs(by[7]).Should().Be(1704164645679L);
	}

	// int64 overflow in tolong: the digits pass the well-formedness regex, then SQLite's CAST(... AS
	// INTEGER) SATURATES an out-of-range integer literal to int64 max (it does NOT null or wrap). Pinned
	// as the observed SQLite behavior; DuckDB's TRY_CAST would instead yield null on the future wave.
	[Fact]
	public async Task Tolong_Int64Overflow_SaturatesToMax_OnSqlite()
	{
		var data = new[] { Rec(1, """{"big":"99999999999999999999"}""") };
		var rows = await Table("events | project Id, V = tolong(Properties.big)", data);
		rows[0][1].Should().Be(9223372036854775807L);
	}

	[Fact]
	public async Task Conversions_OverScalarColumns()
	{
		var data = new[] { Rec(7, "{}", level: (int)LogLevel.Error) };
		var rows = await Table("events | project Id, L = toint(Level), S = tostring(Id), D = todouble(Level)", data);
		rows[0][1].Should().Be(4L);       // toint(Level)
		rows[0][2].Should().Be("7");      // tostring(Id)
		rows[0][3].Should().Be(4.0);      // todouble(Level)
	}

	[Fact]
	public async Task Summarize_SumOfConvertedProperty_SkipsNulls()
	{
		var rows = await Table("events | summarize Total = sum(toint(Properties.Status))", ConvData);
		// 200 + 500, the malformed row contributes nothing
		rows[0][0].Should().Be(700L);
	}

	// ---- nullable numeric CONTEXTS over a converted property (kql-nullable-numeric-contexts) ----
	// Every bag value is TEXT, so numeric analysis goes through toint/tolong/todouble → Nullable<T>. bin(),
	// arithmetic and unary +/- must therefore ACCEPT a nullable numeric and PROPAGATE the null (Kusto/SQL
	// semantics): the unparseable row is neither silently coalesced to 0 (that would skew aggregates) nor
	// allowed to fail the whole query.

	// Two parseable rows in different buckets, one in the same bucket as the first, plus an unparseable and a
	// missing value → the null bucket.
	static IReadOnlyList<LogEntryRecord> CharsData =>
	[
		Rec(1, """{"RespChars":"1200"}"""),
		Rec(2, """{"RespChars":"6000"}"""),
		Rec(3, """{"RespChars":"4999"}"""),
		Rec(4, """{"RespChars":"n/a"}"""),  // unparseable → null
		Rec(5, "{}"),                        // missing → null
	];

	[Fact]
	public async Task Bin_OverConvertedProperty_Histograms_WithNullBucket()
	{
		var rows = await Table(
			"events | extend rc = toint(Properties.RespChars) | summarize C = count() by Bucket = bin(rc, 5000)",
			CharsData);
		var buckets = rows.Select(r => (Key: r[0], Count: (long)r[1]!)).ToList();
		buckets.Should().HaveCount(3);
		buckets.Should().Contain(b => Equals(b.Key, 0L) && b.Count == 2);      // 1200, 4999
		buckets.Should().Contain(b => Equals(b.Key, 5000L) && b.Count == 1);   // 6000
																			   // 'n/a' + missing → ONE null bucket: not an error, and NOT folded into bucket 0.
		buckets.Should().Contain(b => b.Key == null && b.Count == 2);
	}

	[Fact]
	public async Task Bin_OverConvertedProperty_AsAProjection_NullsPropagate()
	{
		var rows = await Table(
			"events | project Id, B = bin(toint(Properties.RespChars), 5000), D = bin(todouble(Properties.RespChars), 2500.0)",
			CharsData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r);
		by[1][1].Should().Be(0L);
		by[2][1].Should().Be(5000L);
		by[2][2].Should().Be(5000.0);
		by[4][1].Should().BeNull();
		by[4][2].Should().BeNull();
		by[5][1].Should().BeNull();
	}

	[Fact]
	public async Task Arithmetic_OverConvertedProperty_SharePct_NullsPropagate()
	{
		var rows = await Table(
			"events | extend chars = toint(Properties.RespChars) | extend sharePct = 100.0 * chars / 12000 "
			+ "| project Id, sharePct, Neg = -chars, Plus = chars + 1",
			CharsData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r);
		((double)by[1][1]!).Should().BeApproximately(10.0, 1e-9);   // 100.0 * 1200 / 12000
		((double)by[2][1]!).Should().BeApproximately(50.0, 1e-9);   // 100.0 * 6000 / 12000
		by[1][2].Should().Be(-1200L);
		by[1][3].Should().Be(1201L);
		// The unparseable / missing rows survive the query and carry nulls through EVERY numeric context.
		by[4][1].Should().BeNull();
		by[4][2].Should().BeNull();
		by[4][3].Should().BeNull();
		by[5][1].Should().BeNull();
	}

	// No hidden coalesce: the null rows contribute NOTHING to sum/avg (they'd drag avg down if binned to 0).
	[Fact]
	public async Task Aggregates_OverConvertedProperty_IgnoreNulls_NoCoalesceToZero()
	{
		var rows = await Table(
			"events | extend chars = toint(Properties.RespChars) | summarize S = sum(chars), A = avg(chars), C = count()",
			CharsData);
		rows[0][0].Should().Be(1200L + 6000L + 4999L);
		((double)rows[0][1]!).Should().BeApproximately((1200.0 + 6000.0 + 4999.0) / 3, 1e-9);
		rows[0][2].Should().Be(5L); // every row is still there — nothing dropped, nothing thrown
	}

	// sum per NULL-able bucket (the reference engine can't serve this — see DualExecutorTests
	// .NullableNumericBin_MatchReference — so it is pinned here): the null bucket aggregates the rows whose
	// value didn't parse, and sums nothing for them.
	[Fact]
	public async Task Aggregates_OverConvertedProperty_PerNullableBin()
	{
		var rows = await Table(
			"events | extend chars = toint(Properties.RespChars) | summarize S = sum(chars), C = count() by Bucket = bin(chars, 5000)",
			CharsData);
		var buckets = rows.Select(r => (Key: r[0], Sum: r[1], Count: (long)r[2]!)).ToList();
		buckets.Should().Contain(b => Equals(b.Key, 0L) && Equals(b.Sum, 1200L + 4999L) && b.Count == 2);
		buckets.Should().Contain(b => Equals(b.Key, 5000L) && Equals(b.Sum, 6000L) && b.Count == 1);
		// The null bucket exists, counts its 2 rows, and sums to null (no value contributed) — NOT to 0.
		buckets.Should().Contain(b => b.Key == null && b.Sum == null && b.Count == 2);
	}

	// The double arm of arithmetic is pinned to the SQL REAL domain (KqlSqlExpressions.AsReal): linq2db prints
	// `100.0` as the bare integer `100`, and SQLite would then run `100 * chars / 8441721` as INTEGER math and
	// truncate the quotient. Two integers still divide as integers (Kusto semantics).
	[Fact]
	public async Task Arithmetic_DoubleOperand_KeepsRealDivision_NotIntegerTruncation()
	{
		var rows = await Table(
			"events | where Id == 3 | project P = 100.0 * toint(Properties.RespChars) / 12000, "
			+ "Q = 100.0 * Id / 12000, IntDiv = toint(Properties.RespChars) / 12000",
			CharsData);
		((double)rows[0][0]!).Should().BeApproximately(100.0 * 4999 / 12000, 1e-9); // 41.658…, not 41
		((double)rows[0][1]!).Should().BeApproximately(100.0 * 3 / 12000, 1e-9);    // 0.025, not 0
		rows[0][2].Should().Be(0L);                                                  // long / long stays integer division
	}

	// A non-numeric operand is still a precise error (the fix widened numeric contexts to Nullable<T>, it did
	// not make them accept anything), and the message speaks KQL types, not CLR ones.
	[Theory]
	[InlineData("events | project X = bin(Properties.RespChars, 5000)", "*bin()*(datetime, timespan) or (numeric, numeric)*string*")]
	[InlineData("events | project X = Message * 2", "*requires a numeric operand*string*")]
	public async Task NonNumericOperand_StillThrowsPrecise(string kql, string message)
	{
		var act = async () => await KqlTestHost.ExecuteAsync(CharsData, Parse(kql), KqlBackend.Sqlite);
		(await act.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage(message);
	}

	// ---- REAL-domain regression net for the OTHER promotion-to-double points (kql-double-literal-real-domain).
	//
	// The defect KqlScalar.RealGuard closes is NARROW and structural: linq2db prints an integral-valued double
	// constant as a bare integer (`100.0` → `100`) and ELIDES Expr.Convert, so a RAW SQL binary operator built
	// from Expr.Multiply/Expr.Divide over a double operand runs as INTEGER math on the dynamically-typed
	// SQLite/DuckDB and TRUNCATES — silently, since the query still succeeds. KqlScalar.Arithmetic is the ONLY
	// place such a raw double-typed binary op is built (every other Expr.Divide/Multiply in the engine is over
	// `long` epoch-ms/ticks/rank constants by design).
	//
	// The three other places a value is PROMOTED to double all sidestep the elision, and these tests pin that:
	//   * bin()'s double arm — the operands are ARGUMENTS to a [Sql.Expression] method with DECLARED double /
	//     double? parameters (BinDouble / BinDoubleN), so linq2db emits them in the real domain and the
	//     division inside the template is real. NEGATIVE values are the discriminator: BinDouble floors toward
	//     -inf, and under integer math `bin(-1, 2.0)` would come back 0 instead of -2.
	//   * iff()/case() branch unification — builds a CASE, no division; a division APPLIED to the result is a
	//     normal Arithmetic node and is RealGuard'ed there.
	//   * mixed-type comparison / join+group keys — SQLite and DuckDB compare INTEGER against REAL numerically,
	//     so no truncation is possible.
	// Aggregate args (sum/avg/percentile) likewise never divide (avg's SQL AVG is real-valued natively).

	// Negative values so the assertion FAILS under integer division: `(0 - Id) / 2` truncates toward zero (→ 0)
	// where the real-domain floor gives -2/-4. Covers the projection AND the `summarize … by` GROUP-BY key.
	[Fact]
	public async Task Bin_IntegralDoubleStep_KeepsRealDomain_NotIntegerTruncation()
	{
		var rows = await Table(
			"events | where Id <= 3 | project Id, Neg = bin(0 - Id, 2.0), Frac = bin(Id, 2.0) / 4, "
			+ "Prop = bin(toint(Properties.Neg), 5000.0)",
			SignedData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r);
		((double)by[1][1]!).Should().Be(-2.0);   // floor(-1/2)*2 — integer math would give 0
		((double)by[3][1]!).Should().Be(-4.0);   // floor(-3/2)*2 — integer math would give -2
		((double)by[3][2]!).Should().Be(0.5);    // bin(3, 2.0) = 2.0, /4 → 0.5 (a FRACTION, not 0)
		((double)by[1][3]!).Should().Be(-5000.0);  // bin(-1200, 5000.0)
		((double)by[2][3]!).Should().Be(-10000.0); // bin(-6000, 5000.0)
		by[3][3].Should().BeNull();                // unparseable → its own null bucket, still no truncation
	}

	[Fact]
	public async Task Bin_IntegralDoubleStep_AsGroupByKey_KeepsRealDomain()
	{
		var rows = await Table("events | summarize C = count() by Bucket = bin(0 - Id, 2.0)", SignedData);
		var buckets = rows.Select(r => (Key: r[0], Count: (long)r[1]!)).ToList();
		// -1,-2 → -2.0 ; -3 → -4.0. Under integer division both would collapse into a single 0 bucket.
		buckets.Should().HaveCount(2);
		buckets.Should().Contain(b => Equals(b.Key, -2.0) && b.Count == 2);
		buckets.Should().Contain(b => Equals(b.Key, -4.0) && b.Count == 1);
	}

	// iff()/case() unify their branches to double; dividing THAT result must stay real (the division is an
	// Arithmetic node, so RealGuard applies). `iff(…, 3, 1) / 2.0` is 1.5 / 0.5 — integer math gives 1 / 0.
	[Fact]
	public async Task IffCase_DoubleUnifiedBranches_KeepRealDivision()
	{
		var rows = await Table(
			"events | where Id <= 2 | project Id, I = iff(Id > 1, 3, 1) / 2.0, "
			+ "C = case(Id > 1, 3, Id > 0, 1, 0) / 2.0, D = iff(Id > 1, toint(Properties.Chars), 1) / 2.0",
			SignedData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r);
		((double)by[1][1]!).Should().Be(0.5);
		((double)by[2][1]!).Should().Be(1.5);
		((double)by[1][2]!).Should().Be(0.5);
		((double)by[2][2]!).Should().Be(1.5);
		((double)by[1][3]!).Should().Be(0.5);
		((double)by[2][3]!).Should().BeApproximately(6000 / 2.0, 1e-9);
	}

	// Aggregate arguments promoted to double: the arg is a RealGuard'ed Arithmetic node, and sum/avg/percentile
	// preserve its fractional value end to end (4999/2 = 2499.5 — integer math would report 2499).
	[Fact]
	public async Task Aggregates_DoubleArgs_KeepFractionalPrecision()
	{
		var rows = await Table(
			"events | summarize S = sum(todouble(Properties.Chars) / 2), A = avg(toint(Properties.Chars)), "
			+ "P = percentile(todouble(Properties.Chars) / 2, 100)",
			SignedData);
		((double)rows[0][0]!).Should().BeApproximately((1200 + 6000 + 4999) / 2.0, 1e-9); // 6099.5, not 6099
		((double)rows[0][1]!).Should().BeApproximately((1200 + 6000 + 4999) / 3.0, 1e-9);
		((double)rows[0][2]!).Should().BeApproximately(6000 / 2.0, 1e-9);
	}

	// A mixed int/double comparison is a NUMERIC comparison on both backends — 4999 > 4999.5 is FALSE. (Were
	// the double literal truncated to 4999 the row would wrongly survive `>` … and this is exactly why the
	// comparison path needs no CAST: comparing is not dividing.)
	[Fact]
	public async Task MixedNumericComparison_ComparesNumerically_NoLiteralTruncation()
	{
		var kept = await Table("events | where toint(Properties.Chars) > 4999.5 | project Id", SignedData);
		kept.Select(r => (long)r[0]!).Should().Equal(2L); // only 6000; 4999 does NOT pass 4999.5
	}

	// Positive + NEGATIVE + unparseable, so the floor-vs-truncate divergence and the null bucket are both live.
	static IReadOnlyList<LogEntryRecord> SignedData =>
	[
		Rec(1, """{"Chars":"1200","Neg":"-1200"}"""),
		Rec(2, """{"Chars":"6000","Neg":"-6000"}"""),
		Rec(3, """{"Chars":"4999","Neg":"oops"}"""),
	];

	// ---- nullable DATETIME bin (kql-bin-nullable-datetime-fix) ----
	// The datetime arm of bin() was the last strict numeric/instant context: todatetime() of a bag value yields
	// DateTime? (long? epoch-ms in the SQL domain), so `bin(todatetime(Properties.T), 1h)` — THE time-histogram
	// idiom over a property-carried timestamp — was rejected outright. It now routes through the
	// null-propagating BinDateTimeMsN / BinDateTimeN, symmetrically with the numeric arm's BinLongN/BinDoubleN:
	// the unparseable and the missing value get their OWN (null) bucket and are NOT coalesced to the epoch
	// (which would invent a phantom 1970 bucket and skew a time histogram).
	// Production-only: KustoLoco tz-shifts datetime literals and models the dynamic bag differently, so
	// todatetime is out of the differential (see this file's header).
	static IReadOnlyList<LogEntryRecord> EventTimeData =>
	[
		Rec(1, """{"T":"2026-04-19T10:17:00Z"}"""),
		Rec(2, """{"T":"2026-04-19T10:59:59Z"}"""),
		Rec(3, """{"T":"2026-04-19T11:02:00Z"}"""),
		Rec(4, """{"T":"not-a-date"}"""),  // unparseable → null
		Rec(5, "{}"),                      // missing → null
	];

	static readonly DateTime H10 = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);
	static readonly DateTime H11 = new(2026, 4, 19, 11, 0, 0, DateTimeKind.Utc);

	[Theory]
	[InlineData("events | project Id, B = bin(todatetime(Properties.T), 1h)")]
	[InlineData("events | extend t = todatetime(Properties.T) | project Id, B = bin(t, 1h)")]
	public async Task BinDateTime_OverConvertedProperty_AsAProjection_NullsPropagate(string kql)
	{
		var rows = await Table(kql, EventTimeData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r[1]);
		by[1].Should().Be(H10);
		by[2].Should().Be(H10);  // 10:59:59 still floors to the top of the hour
		by[3].Should().Be(H11);
		// The query SURVIVES the bad rows — no throw, no hidden coalesce to the epoch.
		by[4].Should().BeNull();
		by[5].Should().BeNull();
	}

	[Theory]
	[InlineData("events | summarize C = count() by Bucket = bin(todatetime(Properties.T), 1h)")]
	[InlineData("events | extend t = todatetime(Properties.T) | summarize C = count() by Bucket = bin(t, 1h)")]
	public async Task BinDateTime_OverConvertedProperty_AsAGroupByKey_HasItsOwnNullBucket(string kql)
	{
		var rows = await Table(kql, EventTimeData);
		var buckets = rows.Select(r => (Key: r[0], Count: (long)r[1]!)).ToList();
		buckets.Should().HaveCount(3);
		buckets.Should().Contain(b => Equals(b.Key, H10) && b.Count == 2);
		buckets.Should().Contain(b => Equals(b.Key, H11) && b.Count == 1);
		// The unparseable + missing rows share ONE null bucket — not a 1970-01-01 bucket, not an error.
		buckets.Should().Contain(b => b.Key == null && b.Count == 2);
	}

	// The bucket floor is the same absolute-tick floor the non-null arm uses (BinDateTimeMs), so a sub-hour
	// step lines up identically; and a NON-converted (non-nullable) Timestamp still takes the strict arm.
	[Fact]
	public async Task BinDateTime_NullableAndNonNullableArms_AgreeOnTheFloor()
	{
		var rows = await Table(
			"events | where Id <= 3 | project Id, Half = bin(todatetime(Properties.T), 30m), Ts = bin(Timestamp, 1h)",
			EventTimeData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r);
		by[1][1].Should().Be(H10);
		by[2][1].Should().Be(H10.AddMinutes(30));  // 10:59:59 → 10:30
		by[3][1].Should().Be(H11);
		by[1][2].Should().Be(H10);                 // the strict (non-nullable) column arm is unchanged
	}

	// ---- bare-name → Properties fallback (no KustoLoco analogue → production-only) ----

	static IReadOnlyList<LogEntryRecord> FallbackData =>
	[
		Rec(1, """{"DeviceId":"dev-a","Status":"200"}"""),
		Rec(2, """{"DeviceId":"dev-b","Status":"500"}"""),
		Rec(3, "{}"),
	];

	[Fact]
	public void BareName_UnknownColumn_ResolvesAsProperty()
	{
		// Status is not an event column → Properties.Status. Numeric compare via toint over the fallback.
		Ids("events | where toint(Status) >= 500", FallbackData).Should().BeEquivalentTo([2L]);
	}

	[Fact]
	public void KnownColumn_AlwaysWins_OverFallback()
	{
		// Level IS a real column → integer comparison, never a Properties lookup.
		Ids("events | where Level == 2", FallbackData).Should().BeEquivalentTo([1L, 2L, 3L]);
	}

	[Fact]
	public async Task BareName_InProjectSummarizeDistinct()
	{
		var proj = await Table("events | where Id == 1 | project Id, Dev = DeviceId", FallbackData);
		proj[0][1].Should().Be("dev-a");

		var summ = await Table("events | summarize C = count() by DeviceId", FallbackData);
		summ.Should().Contain(r => (string?)r[0] == "dev-a" && (long)r[1]! == 1);
		summ.Should().Contain(r => (string?)r[0] == "dev-b" && (long)r[1]! == 1);

		var dist = await Table("events | distinct DeviceId", FallbackData);
		dist.Select(r => r[0] as string).Should().BeEquivalentTo(["dev-a", "dev-b", null]);
	}

	[Fact]
	public async Task BareName_AfterShapeChangeWithoutProperties_KeepsPreciseError()
	{
		// summarize drops PropertiesJson, so an unknown name post-split is a real error, not a fallback.
		var act = async () => await KqlTestHost.ExecuteAsync(FallbackData,
			Parse("events | summarize C = count() by ServiceKey | where NotAColumn == 'x'"), KqlBackend.Sqlite);
		(await act.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage("*unknown column 'NotAColumn'*");
	}

	// ---- precise errors preserved ----

	[Theory]
	[InlineData("events | project X = toint()", "*toint()*1 argument*")]
	[InlineData("events | project X = tolong(Properties.a, 2)", "*tolong()*1 argument*")]
	[InlineData("events | project X = todouble()", "*todouble()*1 argument*")]
	[InlineData("events | project X = tobool(Level)", "*tobool()*bool*")]
	[InlineData("events | project X = todatetime(Level)", "*todatetime()*string or datetime*")]
	[InlineData("events | project X = tostring(Timestamp)", "*tostring()*string, integer, and boolean*")]
	public async Task InvalidConversionCalls_ThrowPrecise(string kql, string message)
	{
		var data = new[] { Rec(1, "{}") };
		var act = async () => await KqlTestHost.ExecuteAsync(data, Parse(kql), KqlBackend.Sqlite);
		(await act.Should().ThrowAsync<UnsupportedKqlException>()).WithMessage(message);
	}
}
