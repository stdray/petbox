using System.Text.Json.Nodes;
using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-side pins (over REAL SQL, both backends via KqlTestHost) for the aggregates that have no
// KustoLoco-differential authority for their EXACT text/semantics: dcount's approx mode (degrade rule +
// small-data equivalence) and make_list/make_set (ordering, null-skip, escaping, cross-backend byte
// identity, empty-input default). The KustoLoco differential (DualExecutorTests.SummarizeMakeListSet_*)
// asserts the same-multiset semantics; these assert the literal production contract.
public sealed class KqlAggregateTests
{
	static LogEntryRecord Rec(long id, int level, string message, string serviceKey = "s", string? exception = null) => new()
	{
		Id = id,
		Level = level,
		Message = message,
		MessageTemplate = message,
		ServiceKey = serviceKey,
		Exception = exception,
		PropertiesJson = "{}",
		TimestampMs = id,
	};

	// --- dcount approx ---

	// SQLite has no approximate distinct primitive, so Approx DEGRADES to the exact COUNT(DISTINCT)
	// (spec kql-semantic-options: degrade, not error) — the two results are IDENTICAL.
	[Fact]
	public async Task DcountApprox_OnSqlite_DegradesToExact()
	{
		var data = new[] { Rec(1, 2, "m", "a"), Rec(2, 2, "m", "b"), Rec(3, 2, "m", "a"), Rec(4, 2, "m", "c") };
		var code = KustoCode.Parse("events | summarize D = dcount(ServiceKey)");

		var (_, exact) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);
		var (_, approx) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite,
			options: new KqlTranslationOptions { DCountMode = KqlDCountMode.Approx });

		approx[0][0].Should().Be(exact[0][0]);
		approx[0][0].Should().Be(3L);
	}

	// DuckDB Approx emits approx_count_distinct (HyperLogLog). It is EXACT for small cardinalities, so on
	// this tiny dataset it equals the exact count — a pin, NOT a contract for large-cardinality sketches.
	[Fact]
	public async Task DcountApprox_OnDuckDb_MatchesExactOnSmallData()
	{
		var data = new[] { Rec(1, 2, "m", "a"), Rec(2, 2, "m", "b"), Rec(3, 2, "m", "a"), Rec(4, 2, "m", "c") };
		var code = KustoCode.Parse("events | summarize D = dcount(ServiceKey)");

		var (_, exact) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb);
		var (_, approx) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb,
			options: new KqlTranslationOptions { DCountMode = KqlDCountMode.Approx });

		approx[0][0].Should().Be(exact[0][0]);
		approx[0][0].Should().Be(3L);
	}

	// --- make_list / make_set ---

	// Value-ascending order is emitted (insertion order b,a,a → ["a","a","b"]) and nulls are SKIPPED
	// (make_list/make_set drop the null Exception), on BOTH backends, byte-for-byte.
	[Theory]
	[InlineData(KqlBackend.Sqlite)]
	[InlineData(KqlBackend.DuckDb)]
	public async Task MakeListSet_ValueAscending_NullSkipped(KqlBackend backend)
	{
		var data = new[]
		{
			Rec(1, 2, "m", exception: "b"),
			Rec(2, 2, "m", exception: "a"),
			Rec(3, 2, "m", exception: null),
			Rec(4, 2, "m", exception: "a"),
		};
		var code = KustoCode.Parse("events | summarize L = make_list(Exception), S = make_set(Exception)");

		var (_, rows) = await KqlTestHost.ExecuteAsync(data, code, backend);

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be("[\"a\",\"a\",\"b\"]");
		rows[0][1].Should().Be("[\"a\",\"b\"]");
	}

	// Escaping (quote / backslash / non-ASCII) and CROSS-BACKEND byte identity: the same query+data yield
	// the IDENTICAL raw JSON text on SQLite and DuckDB, and the text is valid JSON that round-trips to the
	// original strings.
	[Fact]
	public async Task MakeList_Escaping_IsByteIdenticalAcrossBackends()
	{
		var data = new[]
		{
			Rec(1, 2, "m", exception: "a\"q"),   // embedded double-quote
			Rec(2, 2, "m", exception: "b\\s"),    // embedded backslash
			Rec(3, 2, "m", exception: "cαβ"),     // non-ASCII (Greek)
		};
		var code = KustoCode.Parse("events | summarize L = make_list(Exception)");

		var (_, sq) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);
		var (_, dk) = await KqlTestHost.ExecuteAsync(data, code, KqlBackend.DuckDb);

		var sqlite = (string)sq[0][0]!;
		var duck = (string)dk[0][0]!;

		duck.Should().Be(sqlite, "make_list JSON text must be byte-identical across backends");
		var arr = JsonNode.Parse(sqlite)!.AsArray().Select(n => (string)n!).ToList();
		arr.Should().BeEquivalentTo(new[] { "a\"q", "b\\s", "cαβ" });
	}

	// Empty input (a no-`by` summarize over a filtered-empty pipeline) yields the literal empty array "[]"
	// (Kusto's empty-array default), NOT null, on both backends.
	[Theory]
	[InlineData(KqlBackend.Sqlite)]
	[InlineData(KqlBackend.DuckDb)]
	public async Task MakeListSet_EmptyInput_YieldsEmptyArray(KqlBackend backend)
	{
		var data = new[] { Rec(1, 2, "m") };
		var code = KustoCode.Parse("events | where Level == 999 | summarize L = make_list(Message), S = make_set(Message)");

		var (_, rows) = await KqlTestHost.ExecuteAsync(data, code, backend);

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be("[]");
		rows[0][1].Should().Be("[]");
	}

	// int (Int32) columns like Level ARE allowed (integers render as integer JSON text, byte-identical
	// across backends) — value-ascending, set-deduped, same as long.
	[Theory]
	[InlineData(KqlBackend.Sqlite)]
	[InlineData(KqlBackend.DuckDb)]
	public async Task MakeListSet_IntArg_Works(KqlBackend backend)
	{
		var data = new[] { Rec(1, 3, "m"), Rec(2, 1, "m"), Rec(3, 2, "m"), Rec(4, 2, "m") };
		var code = KustoCode.Parse("events | summarize L = make_list(Level), S = make_set(Level)");

		var (_, rows) = await KqlTestHost.ExecuteAsync(data, code, backend);

		rows.Should().HaveCount(1);
		rows[0][0].Should().Be("[1,2,2,3]");
		rows[0][1].Should().Be("[1,2,3]");
	}

	// v1 arg-type restriction: a datetime selector (stored epoch-ms, would diverge across backends) is
	// rejected up front rather than silently producing backend-divergent numbers.
	[Fact]
	public async Task MakeList_RejectsDatetimeArg()
	{
		var data = new[] { Rec(1, 2, "m") };
		var code = KustoCode.Parse("events | summarize L = make_list(Timestamp)");

		var act = async () => await KqlTestHost.ExecuteAsync(data, code, KqlBackend.Sqlite);

		await act.Should().ThrowAsync<UnsupportedKqlException>();
	}
}
