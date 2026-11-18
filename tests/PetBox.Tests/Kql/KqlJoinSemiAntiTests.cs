using Kusto.Language;

namespace PetBox.Tests.Kql;

// `join kind=leftsemi` / `kind=leftanti` — the "what is MISSING" questions ("services with no errors in the
// last 24h", "traces with no spans"). Both project the LEFT columns only and emit each left row AT MOST ONCE.
//
// Every case runs over KqlBackendConfig.Active, so a dialect that composes the EXISTS subquery differently
// goes red here. The one SQL path builds both kinds the same way (Queryable.Any over the null-key-filtered
// right side, see ComposeJoin/BuildSemiAntiJoin) — the multi-backend loop is what proves the two providers
// agree, since the old in-memory StreamJoinRows fallback no longer exists (spec kql-single-path-impl).
//
// NULL-KEY RULE PINNED HERE (Kusto's, not SQL's accident): a null join key never matches anything — including
// another null key. So a left row whose key is null is UNMATCHED: absent from leftsemi, PRESENT in leftanti.
// That is the same rule the shipped leftouter/lookup path already encodes (only the RIGHT side's null keys
// are pre-filtered, so a left null-key row survives unmatched), and Kusto's null semantics: a comparison with
// null evaluates to null, never to true (kusto/query/scalar-data-types/null-values).
public sealed class KqlJoinSemiAntiTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static LogEntryRecord Rec(long id, LogLevel level, string msg, string svc, string props = "{}") => new()
	{
		Id = id,
		TimestampMs = id * 1000,
		Level = (int)level,
		Message = msg,
		MessageTemplate = msg,
		ServiceKey = svc,
		PropertiesJson = props,
	};

	// Two svc-a rows, two svc-b rows, one svc-c row; errors (Level==4) on Id2, Id4, Id5.
	static readonly LogEntryRecord[] Rows =
	[
		Rec(1, LogLevel.Information, "a", "svc-a"),
		Rec(2, LogLevel.Error, "b", "svc-a"),
		Rec(3, LogLevel.Warning, "c", "svc-b"),
		Rec(4, LogLevel.Error, "d", "svc-b"),
		Rec(5, LogLevel.Error, "e", "svc-c"),
	];

	// Region is a Properties key: Id2 and Id4 carry NO Region, so their join key is NULL on both sides.
	static readonly LogEntryRecord[] NullKeyRows =
	[
		Rec(1, LogLevel.Information, "a", "svc", """{"Region":"eu"}"""),
		Rec(2, LogLevel.Information, "b", "svc"), // null key
		Rec(3, LogLevel.Warning, "c", "svc", """{"Region":"eu"}"""),
		Rec(4, LogLevel.Warning, "d", "svc"), // null key — also the right side's null-key row
	];

	// The one production run seam: seed `rows` into a fresh real store per backend and execute `kql`.
	static async Task ForEachBackend(
		LogEntryRecord[] rows, string kql, Action<IReadOnlyList<KqlColumn>, List<object?[]>, KqlBackend> assert)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, result) = await KqlTestHost.ExecuteAsync(rows, Parse(kql), backend);
			assert(cols, result, backend);
		}
	}

	static int Col(IReadOnlyList<KqlColumn> cols, string name)
	{
		for (var i = 0; i < cols.Count; i++)
			if (cols[i].Name == name) return i;
		throw new Xunit.Sdk.XunitException($"column '{name}' not found in [{string.Join(", ", cols.Select(c => c.Name))}]");
	}

	static List<long> Ids(IReadOnlyList<KqlColumn> cols, List<object?[]> rows) =>
		rows.Select(r => (long)r[Col(cols, "Id")]!).OrderBy(x => x).ToList();

	// --- leftsemi: left rows that DO have a match, left columns only, once each ---

	[Fact]
	public async Task LeftSemi_KeepsEveryMatchedLeftRow_OncePerLeftRow()
	{
		// right = the error rows; svc-a (Id1,Id2), svc-b (Id3,Id4) and svc-c (Id5) all have one → all 5 left
		// rows. Duplicate right keys (svc-b has Id4 alone here, but svc-a/svc-b each keep BOTH left rows) must
		// not multiply any left row: each left row appears exactly once.
		await ForEachBackend(Rows, "events | join kind=leftsemi (events | where Level == 4) on ServiceKey",
			(cols, rows, backend) =>
			{
				Ids(cols, rows).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4, 5 }, $"leftsemi on {backend}");
				rows.Should().HaveCount(5, $"each left row at most once on {backend}");
			});
	}

	[Fact]
	public async Task LeftSemi_RightWithDuplicateKeys_DoesNotDuplicateLeftRows()
	{
		// right = BOTH svc-a rows; left = only Id1 (svc-a). A plain inner join would emit Id1 twice.
		var rows = Rows;
		await ForEachBackend(rows, "events | where Id == 1 | join kind=leftsemi (events | where ServiceKey == 'svc-a') on ServiceKey",
			(cols, result, backend) =>
			{
				Ids(cols, result).Should().BeEquivalentTo(new long[] { 1 }, $"one row per left row on {backend}");
				result.Should().HaveCount(1);
			});
	}

	[Fact]
	public async Task LeftSemi_EmptyRight_ReturnsNothing()
	{
		await ForEachBackend(Rows, "events | join kind=leftsemi (events | where Level == 99) on ServiceKey",
			(cols, rows, backend) => rows.Should().BeEmpty($"empty right side on {backend}"));
	}

	// --- leftanti: left rows with NO match, left columns only ---

	[Fact]
	public async Task LeftAnti_ReturnsOnlyUnmatchedLeftRows()
	{
		// right = svc-a rows → svc-b (Id3,Id4) and svc-c (Id5) survive; svc-a (Id1,Id2) are matched out.
		await ForEachBackend(Rows, "events | join kind=leftanti (events | where ServiceKey == 'svc-a') on ServiceKey",
			(cols, rows, backend) => Ids(cols, rows).Should().BeEquivalentTo(new long[] { 3, 4, 5 }, $"leftanti on {backend}"));
	}

	[Fact]
	public async Task LeftAnti_EmptyRight_ReturnsAllLeftRows()
	{
		await ForEachBackend(Rows, "events | join kind=leftanti (events | where Level == 99) on ServiceKey",
			(cols, rows, backend) => Ids(cols, rows).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4, 5 }, $"empty right side on {backend}"));
	}

	// --- null keys: null never matches, including another null ---

	[Fact]
	public async Task LeftSemi_NullLeftKey_NeverMatchesEvenANullRightKey()
	{
		// right = Id3 (Region eu) + Id4 (NO Region). Left Id1/Id3 match on eu; left Id2 and Id4 have a null
		// key — unmatched, so semi drops them even though the RIGHT side also has a null-key row.
		await ForEachBackend(NullKeyRows, "events | join kind=leftsemi (events | where Id >= 3) on Region",
			(cols, rows, backend) => Ids(cols, rows).Should().BeEquivalentTo(new long[] { 1, 3 }, $"null key never matches on {backend}"));
	}

	[Fact]
	public async Task LeftAnti_NullLeftKey_IsReturnedAsUnmatched()
	{
		// The complement of the case above: the null-key left rows (Id2, Id4) have NO match, so leftanti keeps
		// them. Filtering the left side's null keys too would silently lose them — that is the trap this pins.
		await ForEachBackend(NullKeyRows, "events | join kind=leftanti (events | where Id >= 3) on Region",
			(cols, rows, backend) => Ids(cols, rows).Should().BeEquivalentTo(new long[] { 2, 4 }, $"null key is unmatched on {backend}"));
	}

	[Fact]
	public async Task SemiAndAnti_PartitionTheLeftSide()
	{
		// Every left row lands in exactly one of the two kinds: disjoint, and together the whole left side.
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (semiCols, semiRows) = await KqlTestHost.ExecuteAsync(
				NullKeyRows, Parse("events | join kind=leftsemi (events | where Id >= 3) on Region"), backend);
			var (antiCols, antiRows) = await KqlTestHost.ExecuteAsync(
				NullKeyRows, Parse("events | join kind=leftanti (events | where Id >= 3) on Region"), backend);
			var semi = Ids(semiCols, semiRows);
			var anti = Ids(antiCols, antiRows);
			semi.Should().NotIntersectWith(anti, $"the two kinds are disjoint on {backend}");
			semi.Concat(anti).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4 }, $"together they cover the left side on {backend}");
		}
	}

	[Fact]
	public async Task LeftSemi_MultiKey_MatchesOnAllKeys()
	{
		// (ServiceKey, Level) is unique per row here, so a self-join matches each row to itself only — the
		// per-key predicates must be ANDed, not ORed, or every same-service row would match.
		await ForEachBackend(Rows, "events | join kind=leftsemi (events) on ServiceKey, Level",
			(cols, rows, backend) =>
			{
				Ids(cols, rows).Should().BeEquivalentTo(new long[] { 1, 2, 3, 4, 5 }, $"multi-key semi on {backend}");
				rows.Should().HaveCount(5);
			});
		await ForEachBackend(Rows, "events | join kind=leftanti (events) on ServiceKey, Level",
			(cols, rows, backend) => rows.Should().BeEmpty($"multi-key anti on {backend}"));
	}

	// --- shape: LEFT columns only (no right column, no collision suffix) and it composes downstream ---

	[Fact]
	public async Task LeftSemiAndAnti_ProjectLeftColumnsOnly()
	{
		foreach (var kql in new[]
		{
			"events | join kind=leftsemi (events | where Level == 4) on ServiceKey",
			"events | join kind=leftanti (events | where Level == 4) on ServiceKey",
		})
		{
			foreach (var backend in KqlBackendConfig.Active)
			{
				var (leftCols, _) = await KqlTestHost.ExecuteAsync(Rows, Parse("events | where Id > 0"), backend);
				var (cols, _) = await KqlTestHost.ExecuteAsync(Rows, Parse(kql), backend);
				cols.Select(c => c.Name).Should().BeEquivalentTo(leftCols.Select(c => c.Name), $"'{kql}' emits the left schema on {backend}");
			}
		}
	}

	[Fact]
	public async Task LeftAnti_ComposesDownstreamInSql()
	{
		await ForEachBackend(Rows, "events | join kind=leftanti (events | where ServiceKey == 'svc-a') on ServiceKey | where Level == 4 | project Id, Message",
			(cols, rows, backend) =>
			{
				cols.Select(c => c.Name).Should().Equal(["Id", "Message"], $"project after leftanti on {backend}");
				Ids(cols, rows).Should().BeEquivalentTo(new long[] { 4, 5 }, $"downstream filter on {backend}");
			});
	}

	// --- the equivalence a user could express today: leftsemi ≡ `where <key> in (...)` ---

	[Fact]
	public async Task LeftSemi_IsEquivalentToWhereKeyInLiteralList()
	{
		// Same value set the right side of the semi join supplies ('svc-a','svc-b','svc-c'), written the only
		// way this engine can express it — a literal list. Pinned because it is the answer the semi join must
		// reproduce, row for row. (A SUBQUERY `in` — `where K in (events | ...)` — is not supported at all:
		// it throws "expression 'PipeExpression' not supported", so the literal list is the whole surface today.)
		const string semi = "events | join kind=leftsemi (events | where Level >= 3) on ServiceKey";
		const string inList = "events | where ServiceKey in ('svc-a', 'svc-b', 'svc-c')";
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (semiCols, semiRows) = await KqlTestHost.ExecuteAsync(Rows, Parse(semi), backend);
			var (inCols, inRows) = await KqlTestHost.ExecuteAsync(Rows, Parse(inList), backend);
			var semiIds = Ids(semiCols, semiRows);
			semiIds.Should().NotBeEmpty($"the fixture must make this a real comparison on {backend}");
			semiIds.Should().BeEquivalentTo(Ids(inCols, inRows), $"leftsemi ≡ where <key> in (...) on {backend}");
		}
	}

	// --- parse surface ---

	[Fact]
	public async Task UnsupportedKind_ErrorListsSemiAndAnti()
	{
		var act = async () => await KqlTestHost.ExecuteAsync(Rows, Parse("events | join kind=fullouter (events) on ServiceKey"), KqlBackend.Sqlite);
		var message = (await act.Should().ThrowAsync<UnsupportedKqlException>()).Which.Message;
		message.Should().Contain("fullouter").And.Contain("leftsemi").And.Contain("leftanti");
	}

	[Fact]
	public async Task UnknownKind_ErrorNamesTheTokenTheUserTyped()
	{
		// Even a kind Kusto.Language does not recognise reaches ParseJoinKind as a literal, so the error names
		// the token the user typed rather than an empty kind.
		var act = async () => await KqlTestHost.ExecuteAsync(Rows, Parse("events | join kind=bogus (events) on ServiceKey"), KqlBackend.Sqlite);
		var message = (await act.Should().ThrowAsync<UnsupportedKqlException>()).Which.Message;
		message.Should().Contain("bogus");
	}
}
