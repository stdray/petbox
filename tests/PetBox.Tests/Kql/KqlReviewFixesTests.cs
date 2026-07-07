using Kusto.Language;
using PetBox.Log.Core.Query;

namespace PetBox.Tests.Kql;

// Production-only regression coverage for the confirmed code-review findings (F1-F10). Each test
// reproduces the specific failure scenario the review called out. SQL-path / differential variants
// live in SqliteKqlIntegrationTests and DualExecutorTests respectively; these pin the semantics the
// reference executor can't express (null exclusion, bare-name casing, the join cap, nullable
// composition) directly against the production engine.
public sealed class KqlReviewFixesTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static readonly DateTime Ts = new(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc);

	static LogEntryRecord Rec(long id, string props = "{}", int level = (int)LogLevel.Information,
		string msg = "m", string svc = "svc") => new()
		{
			Id = id,
			ServiceKey = svc,
			TimestampMs = new DateTimeOffset(Ts, TimeSpan.Zero).ToUnixTimeMilliseconds(),
			Level = level,
			Message = msg,
			MessageTemplate = msg,
			PropertiesJson = props,
		};

	static IReadOnlyList<long> Ids(string kql, IReadOnlyList<LogEntryRecord> data) =>
		KqlTransformer.Apply(data.AsQueryable(), Parse(kql)).ToList().Select(r => r.Id).ToList();

	static async Task<List<object?[]>> Table(string kql, IReadOnlyList<LogEntryRecord> data)
	{
		var result = KqlTransformer.Execute(data.AsQueryable(), Parse(kql));
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	static async Task<List<object?[]>> TableFrom(KqlResult result)
	{
		var rows = new List<object?[]>();
		await foreach (var r in result.Rows) rows.Add(r);
		return rows;
	}

	static int Col(KqlResult r, string name)
	{
		for (var i = 0; i < r.Columns.Count; i++)
			if (r.Columns[i].Name == name) return i;
		throw new Xunit.Sdk.XunitException($"column '{name}' not in [{string.Join(", ", r.Columns.Select(c => c.Name))}]");
	}

	// ---- F1: null unboxing — leftouter/lookup unmatched right columns and all-null aggregates ----

	static readonly LogEntryRecord[] JoinRows =
	[
		Rec(1, svc: "svc-a", level: (int)LogLevel.Information),
		Rec(2, svc: "svc-a", level: (int)LogLevel.Error),
		Rec(3, svc: "svc-b", level: (int)LogLevel.Warning),
		Rec(4, svc: "svc-b", level: (int)LogLevel.Error),
		Rec(5, svc: "svc-c", level: (int)LogLevel.Error),
	];

	[Fact]
	public async Task F1_LeftOuter_ComparingNullRightColumn_ExcludesInsteadOfCrashing()
	{
		// Right side is empty (Level == 99), so every left row is unmatched → Id1 is null. A `where Id1 > 0`
		// must unbox the (now nullable) Id1 cell as null → excluded, NOT NRE-crash on an object→long unbox.
		var result = KqlTransformer.Execute(JoinRows.AsQueryable(),
			Parse("events | join kind=leftouter (events | where Level == 99) on ServiceKey | where Id1 > 0"));
		(await TableFrom(result)).Should().BeEmpty();
	}

	[Fact]
	public async Task F1_LeftOuter_MatchedNullableRightColumn_StillComparesByValue()
	{
		// A matched leftouter row keeps a real Id1 value even though the column is now nullable.
		var result = KqlTransformer.Execute(JoinRows.AsQueryable(),
			Parse("events | join kind=leftouter (events | where Id == 2) on ServiceKey | where Id1 == 2 | project Id"));
		var rows = await TableFrom(result);
		rows.Select(r => (long)r[0]!).Should().BeEquivalentTo([1L, 2L]); // both svc-a left rows matched right Id2
	}

	[Fact]
	public async Task F1_AvgOverAllNullGroup_IsNull_AndDownstreamWhereExcludesIt()
	{
		var data = new[]
		{
			Rec(1, """{"X":"5"}""", svc: "svc-a"),
			Rec(2, """{"X":"7"}""", svc: "svc-a"),
			Rec(3, """{"X":"nope"}""", svc: "svc-b"), // unparseable → null arg
			Rec(4, "{}", svc: "svc-b"),               // missing → null arg
		};
		// svc-b's avg is over all-null args → null result. `| where A > 0` must not crash and must drop it.
		var result = KqlTransformer.Execute(data.AsQueryable(),
			Parse("events | summarize A = avg(toint(Properties.X)) by ServiceKey | where A > 0"));
		var rows = await TableFrom(result);
		rows.Should().ContainSingle();
		rows[0][Col(result, "ServiceKey")].Should().Be("svc-a");
		((double)rows[0][Col(result, "A")]!).Should().Be(6.0);

		// Without the downstream where, the null group still materializes (no crash), with A = null.
		var all = await Table("events | summarize A = avg(toint(Properties.X)) by ServiceKey", data);
		all.Should().HaveCount(2);
		all.Single(r => (string?)r[0] == "svc-b")[1].Should().BeNull();
	}

	[Fact]
	public async Task F1_MinMaxOverEmptyGroupNumericColumn_IsNull_NoCrash()
	{
		var data = new[] { Rec(1, svc: "svc-a", level: (int)LogLevel.Error) };
		// min/max declare nullable result types; filtering them by a computed predicate must not crash.
		var result = KqlTransformer.Execute(data.AsQueryable(),
			Parse("events | where Level == 99 | summarize M = min(Id) by ServiceKey | where M > 0"));
		(await TableFrom(result)).Should().BeEmpty();
	}

	// ---- F2: join keys compare by value across int/long ----

	[Fact]
	public async Task F2_JoinKey_LongVsInt_MatchesByValue()
	{
		// Left key K is a toint()-computed long; right key Level is a raw int. Before the normalization
		// fix, GroupKey's boxed Equals(4L, 4) was false, so the join silently produced NOTHING.
		var result = KqlTransformer.Execute(JoinRows.AsQueryable(),
			Parse("events | extend K = toint(Level) | join kind=inner (events) on $left.K == $right.Level | project Id, Id1"));
		var rows = await TableFrom(result);
		var id = Col(result, "Id");
		var id1 = Col(result, "Id1");
		rows.Select(r => ((long)r[id]!, (long)r[id1]!)).Should().BeEquivalentTo(
			[(1L, 1L), (2L, 2L), (2L, 4L), (2L, 5L), (3L, 3L),
			 (4L, 2L), (4L, 4L), (4L, 5L), (5L, 2L), (5L, 4L), (5L, 5L)]);
	}

	// ---- F3: != / !in / !between exclude null operands (both record and row contexts) ----

	static IReadOnlyList<LogEntryRecord> StatusData =>
	[
		Rec(1, """{"Status":"200"}"""),
		Rec(2, """{"Status":"500"}"""),
		Rec(3, """{"Status":"nope"}"""), // unparseable → null
		Rec(4, "{}"),                     // missing → null
	];

	[Fact]
	public void F3_NotEqual_OverNull_ExcludesRow_RecordContext()
	{
		// `!= 200`: 200 excluded (equal), 500 kept, the two null rows must be EXCLUDED (Kusto null
		// semantics) — C#'s lifted `!=` would wrongly KEEP them. This runs the record/SQL-path tree.
		Ids("events | where toint(Properties.Status) != 200", StatusData).Should().BeEquivalentTo([2L]);
	}

	[Fact]
	public async Task F3_NotEqual_OverNull_ExcludesRow_RowContext()
	{
		// Same predicate AFTER a shape change → the in-memory RowScalarContext path. Must agree with SQL.
		var rows = await Table("events | extend Z = 1 | where toint(Properties.Status) != 200 | project Id", StatusData);
		rows.Select(r => (long)r[0]!).Should().BeEquivalentTo([2L]);
	}

	[Fact]
	public void F3_NotIn_OverNull_ExcludesRow()
	{
		Ids("events | where toint(Properties.Status) !in (200)", StatusData).Should().BeEquivalentTo([2L]);
	}

	[Fact]
	public void F3_NotBetween_OverNull_ExcludesRow()
	{
		Ids("events | where toint(Properties.Status) !between (100 .. 300)", StatusData).Should().BeEquivalentTo([2L]);
	}

	// ---- F4: join build side is capped ----

	[Fact]
	public async Task F4_JoinBuildSide_ExceedingCap_ThrowsTeachingError()
	{
		KqlTransformer.JoinBuildSideCapOverride = 2;
		try
		{
			// Right side has 5 rows > cap of 2 → fail fast with a teaching message.
			// NOTE (kql-single-path-impl): ALL join kinds now compile to SQL when the right side is fully
			// SQL-composable, so the in-memory build-side cap is MOOT for those (the DB does the join). It is
			// still reachable — and pinned here — when the right side FALLS BACK to the in-memory hash join,
			// which a post-split `where` (not yet migrated) forces: `project … | where …`. RESULT is
			// unchanged. The cap code stays until the whole in-memory tail is deleted (every op migrated).
			var result = KqlTransformer.Execute(JoinRows.AsQueryable(),
				Parse("events | join kind=inner (events | project ServiceKey, Id | where Id >= 1) on ServiceKey | project Id, Id1"));
			var act = async () => await TableFrom(result);
			(await act.Should().ThrowAsync<UnsupportedKqlException>())
				.WithMessage("*join right side exceeded 2 rows*narrow it with where/take*");
		}
		finally
		{
			KqlTransformer.JoinBuildSideCapOverride = null;
		}
	}

	[Fact]
	public async Task F4_JoinBuildSide_WithinCap_Succeeds()
	{
		KqlTransformer.JoinBuildSideCapOverride = 100;
		try
		{
			var result = KqlTransformer.Execute(JoinRows.AsQueryable(),
				Parse("events | join kind=inner (events) on Id | project Id, Id1"));
			(await TableFrom(result)).Should().HaveCount(5);
		}
		finally
		{
			KqlTransformer.JoinBuildSideCapOverride = null;
		}
	}

	// ---- F5: extend / project see columns introduced earlier in the same operator ----

	[Fact]
	public async Task F5_Extend_SeesEarlierColumnInSameOperator()
	{
		var data = new[] { Rec(1, level: (int)LogLevel.Information), Rec(2, level: (int)LogLevel.Error) };
		var rows = await Table("events | extend A = tostring(Level), B = strcat(A, '-x') | project Id, A, B", data);
		var by = rows.ToDictionary(r => (long)r[0]!, r => ((string?)r[1], (string?)r[2]));
		by[1L].Should().Be(("2", "2-x"));
		by[2L].Should().Be(("4", "4-x"));
	}

	[Fact]
	public async Task F5_Extend_ReplacedColumn_VisibleToLaterExpression()
	{
		var data = new[] { Rec(1, level: (int)LogLevel.Information) };
		// Level is replaced in place, then referenced again — the later expression sees the NEW value.
		var rows = await Table("events | extend Level = Level + 100, X = Level | project Id, Level, X", data);
		rows[0][1].Should().Be(102L);
		rows[0][2].Should().Be(102L);
	}

	[Fact]
	public async Task F5_Project_SeesEarlierProjectedColumn()
	{
		var data = new[] { Rec(1, level: (int)LogLevel.Error) };
		var rows = await Table("events | project Id, A = tostring(Level), B = strcat(A, '!')", data);
		rows[0][1].Should().Be("4");
		rows[0][2].Should().Be("4!");
	}

	// ---- F6: bare names resolve to a real column case-insensitively; true typos fall to Properties ----

	static readonly LogEntryRecord[] CasingData =
	[
		Rec(1, """{"DeviceId":"dev-a"}""", level: (int)LogLevel.Error, msg: "boom", svc: "web"),
		Rec(2, """{"DeviceId":"dev-b"}""", level: (int)LogLevel.Information, msg: "hello", svc: "api"),
	];

	[Fact]
	public void F6_LowercaseColumnName_ResolvesToRealColumn_RecordContext()
	{
		Ids("events | where level == 4", CasingData).Should().BeEquivalentTo([1L]);
		Ids("events | where servicekey == 'web'", CasingData).Should().BeEquivalentTo([1L]);
		Ids("events | where MESSAGE == 'hello'", CasingData).Should().BeEquivalentTo([2L]);
	}

	[Fact]
	public async Task F6_LowercaseColumnName_ResolvesToRealColumn_RowContext()
	{
		var rows = await Table("events | extend Z = 1 | where level == 4 | project Id", CasingData);
		rows.Select(r => (long)r[0]!).Should().BeEquivalentTo([1L]);
	}

	[Fact]
	public void F6_TrueTypo_FallsToProperties_YieldsNoMatch()
	{
		// 'ServiceKye' has no case-insensitive column match → Properties.ServiceKye (absent) → zero rows.
		Ids("events | where ServiceKye == 'web'", CasingData).Should().BeEmpty();
	}

	[Fact]
	public async Task F6_CaseInsensitive_InSummarizeAndDistinct()
	{
		var summ = await Table("events | summarize C = count() by servicekey", CasingData);
		summ.Should().HaveCount(2);
		var dist = await Table("events | distinct level", CasingData);
		dist.Select(r => Convert.ToInt64(r[0])).Should().BeEquivalentTo([2L, 4L]);
	}

	// ---- F10: nullable typed-conversion results compose ----

	[Fact]
	public async Task F10_ToString_OverNullableConversion_NullPropagates()
	{
		var rows = await Table("events | project Id, S = tostring(toint(Properties.Status))", StatusData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => (string?)r[1]);
		by[1L].Should().Be("200");
		by[2L].Should().Be("500");
		by[3L].Should().BeNull(); // toint('nope') = null → tostring = null
		by[4L].Should().BeNull(); // missing → null
	}

	[Fact]
	public async Task F10_Iff_WithNullableBranch_UnifiesToNullable()
	{
		// iff(cond, toint(x), 0): branches are long? and long — unify to long? rather than throwing
		// "incompatible result types".
		var rows = await Table("events | project Id, V = iff(Id == 1, toint(Properties.Status), 0)", StatusData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r[1]);
		by[1L].Should().Be(200L);
		by[2L].Should().Be(0L);
	}

	[Fact]
	public void F10_ToString_OverNullableConversion_InWhere()
	{
		Ids("events | where tostring(toint(Properties.Status)) == '500'", StatusData).Should().BeEquivalentTo([2L]);
	}

	// ---- F7: tobool over a JSON boolean, in-memory path (json_extract renders true/false as text) ----

	[Theory]
	[InlineData("true", true)]
	[InlineData("false", false)]
	public async Task F7_ToBool_OverJsonBoolean_InMemory(string jsonBool, bool expected)
	{
		var data = new[] { Rec(1, $$"""{"Enabled":{{jsonBool}}}""") };
		var rows = await Table("events | extend Z = 1 | project Id, B = tobool(Properties.Enabled)", data);
		rows[0][1].Should().Be(expected);
	}

	// ---- F8: negative substring start clamps to 0 in-memory (mirrors the SQL clamp) ----

	[Fact]
	public async Task F8_Substring_NegativeStart_ClampedInMemory()
	{
		var data = new[] { Rec(1, msg: "hello") };
		var rows = await Table("events | project Id, S = substring(Message, -3, 2)", data);
		rows[0][1].Should().Be("he"); // start clamped to 0, length 2
	}

	[Fact]
	public async Task F10_Case_WithNullableBranch_Composes()
	{
		var rows = await Table(
			"events | project Id, V = case(Id == 1, toint(Properties.Status), Id == 2, todouble(Properties.Status), 0.0)",
			StatusData);
		var by = rows.ToDictionary(r => (long)r[0]!, r => r[1]);
		by[1L].Should().Be(200.0); // unified to double? across long?/double?/double branches
		by[2L].Should().Be(500.0);
	}
}
