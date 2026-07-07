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
