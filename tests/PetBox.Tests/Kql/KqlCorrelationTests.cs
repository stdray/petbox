using Kusto.Language;

namespace PetBox.Tests.Kql;

// Production-only coverage for the wave-5 correlation ops. join/lookup are ALSO differential-tested
// against the reference executor (DualExecutorTests.Join_MatchesReference / Lookup_MatchesReference);
// the cases here pin the semantics the reference executor can't express or where its dynamic model
// diverges: innerunique dedup choice, collision-suffix scheme, empty/leftouter-null shapes, and the
// whole of mv-expand and parse (the reference executor can't expand Properties-JSON arrays and does
// not implement `parse`).
//
// Converted to run production over the SHARED real-SQLite harness (KqlTestHost) instead of the
// EnumerableQuery provider. NOTE: mv-expand is NOT yet SQL-migrated — the SQL prefix runs on SQLite and
// falls back to the in-memory mv-expand tail; results agree with the old in-memory behavior (verified
// below), no re-pin was required for the mv-expand cases.
public sealed class KqlCorrelationTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	static LogEntryRecord Rec(long id, LogLevel level, string msg, string svc = "svc", string props = "{}") => new()
	{
		Id = id,
		TimestampMs = id * 1000,
		Level = (int)level,
		Message = msg,
		MessageTemplate = msg,
		ServiceKey = svc,
		PropertiesJson = props,
	};

	// The one production run seam: seed `rows` into a fresh in-memory LogDb and Execute `kql` over the
	// real linq2db IQueryable, fully materialized. Sqlite is the only Active backend today.
	static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> Exec(IEnumerable<LogEntryRecord> rows, string kql) =>
		KqlTestHost.ExecuteAsync(rows.ToArray(), Parse(kql), KqlBackend.Sqlite);

	static int Col(IReadOnlyList<KqlColumn> cols, string name)
	{
		for (var i = 0; i < cols.Count; i++)
			if (cols[i].Name == name) return i;
		throw new Xunit.Sdk.XunitException($"column '{name}' not found in [{string.Join(", ", cols.Select(c => c.Name))}]");
	}

	// --- join ---

	static readonly LogEntryRecord[] JoinRows =
	[
		Rec(1, LogLevel.Information, "a", "svc-a"),
		Rec(2, LogLevel.Error, "b", "svc-a"),
		Rec(3, LogLevel.Warning, "c", "svc-b"),
		Rec(4, LogLevel.Error, "d", "svc-b"),
		Rec(5, LogLevel.Error, "e", "svc-c"),
	];

	[Fact]
	public async Task Join_Inner_EmitsEveryMatch()
	{
		// Error rows (Level==4) = Id2(svc-a), Id4(svc-b), Id5(svc-c); right Level>=3 = Id2,Id3,Id4,Id5.
		var (cols, rows) = await Exec(JoinRows, "events | where Level == 4 | join kind=inner (events | where Level >= 3) on ServiceKey");
		var id = Col(cols, "Id");
		var id1 = Col(cols, "Id1");
		var pairs = rows.Select(r => ((long)r[id]!, (long)r[id1]!)).ToList();
		// svc-a right = {Id2}; svc-b right = {Id3,Id4}; svc-c right = {Id5}.
		pairs.Should().BeEquivalentTo([(2L, 2L), (4L, 3L), (4L, 4L), (5L, 5L)]);
	}

	[Fact]
	public async Task Join_Default_IsInnerUnique_DedupsLeftKeepingFirst()
	{
		// default kind = innerunique: left de-duplicated by ServiceKey, first row per key wins.
		var (cols, rows) = await Exec(JoinRows, "events | join (events | where Level == 4) on ServiceKey");
		var id = Col(cols, "Id");
		var id1 = Col(cols, "Id1");
		var pairs = rows.Select(r => ((long)r[id]!, (long)r[id1]!)).ToList();
		// left deduped: svc-a→Id1, svc-b→Id3, svc-c→Id5. right Level==4 = Id2(svc-a),Id4(svc-b),Id5(svc-c).
		pairs.Should().BeEquivalentTo([(1L, 2L), (3L, 4L), (5L, 5L)]);
	}

	[Fact]
	public async Task Join_InnerUnique_KeepsFirstLeftRow_ByInputOrder()
	{
		// Explicit innerunique against a right that has two svc-a rows: left svc-a deduped to Id1.
		var (cols, rows) = await Exec(JoinRows, "events | join kind=innerunique (events | where ServiceKey == 'svc-a') on ServiceKey");
		var id = Col(cols, "Id");
		var id1 = Col(cols, "Id1");
		var pairs = rows.Select(r => ((long)r[id]!, (long)r[id1]!)).ToList();
		// left svc-a rows {1,2} → dedup keeps Id1; joined to right svc-a rows {1,2}.
		pairs.Should().BeEquivalentTo([(1L, 1L), (1L, 2L)]);
	}

	[Fact]
	public async Task Join_LeftOuter_KeepsUnmatchedLeftWithNullRight()
	{
		// right = only Id2 (svc-a), so svc-b and svc-c left rows are unmatched → null right Id.
		var (cols, rows) = await Exec(JoinRows, "events | join kind=leftouter (events | where Id == 2) on ServiceKey");
		var id = Col(cols, "Id");
		var id1 = Col(cols, "Id1");
		var byLeft = rows.GroupBy(r => (long)r[id]!).ToDictionary(g => g.Key, g => g.Select(r => r[id1]).ToList());
		byLeft[1L].Should().BeEquivalentTo([2L]); // svc-a → right Id2
		byLeft[2L].Should().BeEquivalentTo([2L]); // svc-a → right Id2
		byLeft[3L].Should().ContainSingle().Which.Should().BeNull(); // svc-b unmatched
		byLeft[5L].Should().ContainSingle().Which.Should().BeNull(); // svc-c unmatched
	}

	[Fact]
	public async Task Join_MultiKey_MatchesOnAllKeys()
	{
		var (cols, rows) = await Exec(JoinRows, "events | join kind=inner (events) on ServiceKey, Level");
		var id = Col(cols, "Id");
		var id1 = Col(cols, "Id1");
		// (ServiceKey,Level) is unique per row here, so a self-join matches each row to itself only.
		rows.Select(r => ((long)r[id]!, (long)r[id1]!)).Should().BeEquivalentTo(
			[(1L, 1L), (2L, 2L), (3L, 3L), (4L, 4L), (5L, 5L)]);
	}

	[Fact]
	public async Task Join_ExplicitLeftRightEquality_Works()
	{
		var (cols, rows) = await Exec(JoinRows, "events | where Id == 2 | join kind=inner (events | where Level >= 3) on $left.ServiceKey == $right.ServiceKey");
		var id = Col(cols, "Id");
		var id1 = Col(cols, "Id1");
		rows.Select(r => ((long)r[id]!, (long)r[id1]!)).Should().BeEquivalentTo([(2L, 2L)]); // svc-a right Level>=3 = Id2
	}

	[Fact]
	public async Task Join_SelfJoin_SuffixesCollidingRightColumns()
	{
		var (cols, _) = await Exec(JoinRows, "events | join kind=inner (events) on Id");
		var names = cols.Select(c => c.Name).ToList();
		// left keeps its names; every right column collides and is suffixed with 1.
		names.Should().ContainInOrder("Id", "ServiceKey", "Message", "Id1", "ServiceKey1", "Message1");
		names.Count(n => n == "Id").Should().Be(1);
		names.Should().Contain("PropertiesJson1");
	}

	[Fact]
	public async Task Join_CollisionSuffix_SkipsAlreadyTakenSuffix()
	{
		// left already carries an 'Id1' column (from extend), so the right Id must land on 'Id2'.
		var (cols, rows) = await Exec(JoinRows, "events | extend Id1 = Level | join kind=inner (events) on Id");
		var names = cols.Select(c => c.Name).ToList();
		names.Should().Contain("Id1");  // the extend column
		names.Should().Contain("Id2");  // the right Id, bumped past the taken Id1
		var id = Col(cols, "Id");
		var id2 = Col(cols, "Id2");
		// self-join on Id → each row matched to itself; right Id equals left Id.
		rows.Should().OnlyContain(r => (long)r[id]! == (long)r[id2]!);
	}

	[Fact]
	public async Task Join_EmptyRight_Inner_ProducesNothing()
	{
		var (_, rows) = await Exec(JoinRows, "events | join kind=inner (events | where Level == 99) on ServiceKey");
		rows.Should().BeEmpty();
	}

	[Fact]
	public async Task Join_EmptyRight_LeftOuter_KeepsAllLeftWithNulls()
	{
		var (cols, rows) = await Exec(JoinRows, "events | join kind=leftouter (events | where Level == 99) on ServiceKey");
		var id1 = Col(cols, "Id1");
		rows.Should().HaveCount(5);
		rows.Should().OnlyContain(r => r[id1] == null);
	}

	[Fact]
	public async Task Join_UnsupportedKind_ThrowsPrecise()
	{
		var act = async () => await Exec(JoinRows, "events | join kind=fullouter (events) on ServiceKey");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*fullouter*");
	}

	[Fact]
	public async Task Join_CrossTableRightSide_ThrowsSameLog()
	{
		var act = async () => await Exec(JoinRows, "events | join kind=inner (systemlog | where Level > 3) on ServiceKey");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*same log*");
	}

	[Fact]
	public async Task Join_NonEqualityOnClause_Throws()
	{
		var act = async () => await Exec(JoinRows, "events | join kind=inner (events) on $left.Level < $right.Level");
		await act.Should().ThrowAsync<UnsupportedKqlException>();
	}

	[Fact]
	public async Task Join_HalfDollarEquality_ThrowsPrecise()
	{
		var act = async () => await Exec(JoinRows, "events | join kind=inner (events) on $left.ServiceKey == ServiceKey");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*$left.col == $right.col*");
	}

	[Fact]
	public async Task Join_OnPropertyBareFallback_ResolvesViaJson()
	{
		var rows = new[]
		{
			Rec(1, LogLevel.Information, "a", "svc-a", """{"Region":"eu"}"""),
			Rec(2, LogLevel.Error, "b", "svc-b", """{"Region":"eu"}"""),
			Rec(3, LogLevel.Warning, "c", "svc-c", """{"Region":"us"}"""),
		};
		// join on the bare 'Region' name → Properties.Region on both sides.
		var (cols, mat) = await Exec(rows, "events | where Id == 1 | join kind=inner (events) on Region");
		var id1 = Col(cols, "Id1");
		mat.Select(r => (long)r[id1]!).Should().BeEquivalentTo([1L, 2L]); // both eu rows
	}

	// --- lookup ---

	[Fact]
	public async Task Lookup_DropsRightKeyColumn_AndEmitsEveryMatch()
	{
		var (cols, rows) = await Exec(JoinRows, "events | where Id == 1 | lookup (events) on ServiceKey");
		var names = cols.Select(c => c.Name).ToList();
		// right ServiceKey (the key) is dropped: there is exactly one ServiceKey column and no ServiceKey1.
		names.Count(n => n == "ServiceKey").Should().Be(1);
		names.Should().NotContain("ServiceKey1");
		names.Should().Contain("Message1"); // non-key right columns survive, suffixed
		var msg1 = Col(cols, "Message1");
		// left Id1 (svc-a) matches both svc-a right rows (Id1 "a", Id2 "b") — leftouter, all matches.
		rows.Select(r => (string?)r[msg1]).Should().BeEquivalentTo(["a", "b"]);
	}

	[Fact]
	public async Task Lookup_Unmatched_KeepsLeftRowWithNulls()
	{
		// Id 6 (not 1): JoinRows already occupies Ids 1-5, and the real-SQLite harness enforces the
		// LogEntries PK, unlike the old EnumerableQuery provider which never checked for collisions.
		var rows = new[] { Rec(6, LogLevel.Information, "a", "lonely") };
		var (cols, mat) = await Exec(JoinRows.Concat(rows), "events | where ServiceKey == 'lonely' | lookup (events | where ServiceKey == 'svc-a') on ServiceKey");
		mat.Should().ContainSingle();
		mat[0][Col(cols, "Message1")].Should().BeNull();
	}

	// --- mv-expand ---

	static readonly LogEntryRecord[] MvRows =
	[
		Rec(1, LogLevel.Information, "a", "svc", """{"Tags":["x","y","z"]}"""),
		Rec(2, LogLevel.Error, "b", "svc", """{"Tags":[]}"""),          // empty array → dropped
		Rec(3, LogLevel.Warning, "c", "svc", """{"Other":"scalar"}"""), // missing Tags → dropped
		Rec(4, LogLevel.Information, "d", "svc", """{"Tags":"notarray"}"""), // non-array → dropped
		Rec(5, LogLevel.Information, "e", "svc", """{"Nums":[10,20]}"""),
	];

	[Fact]
	public async Task MvExpand_Property_OneRowPerElement()
	{
		var (cols, rows) = await Exec(MvRows, "events | mv-expand Properties.Tags | project Id, Tags");
		cols.Select(c => c.Name).Should().ContainInOrder("Id", "Tags");
		// only Id1 has a real array of 3; the empty/missing/non-array rows are dropped.
		rows.Select(r => ((long)r[0]!, (string?)r[1])).Should().BeEquivalentTo(
			[(1L, "x"), (1L, "y"), (1L, "z")]);
	}

	[Fact]
	public async Task MvExpand_BareName_FallsBackToProperty()
	{
		var (_, rows) = await Exec(MvRows, "events | mv-expand Tags | project Id, Tags");
		rows.Select(r => ((long)r[0]!, (string?)r[1])).Should().BeEquivalentTo(
			[(1L, "x"), (1L, "y"), (1L, "z")]);
	}

	[Fact]
	public async Task MvExpand_NumericElements_AreStringTyped()
	{
		var (cols, rows) = await Exec(MvRows, "events | mv-expand Nums | project Id, Nums");
		cols[Col(cols, "Nums")].ClrType.Should().Be<string>();
		rows.Select(r => ((long)r[0]!, (string?)r[1])).Should().BeEquivalentTo(
			[(5L, "10"), (5L, "20")]);
	}

	[Fact]
	public async Task MvExpand_MissingAndEmptyAndNonArray_DropRows()
	{
		var (_, rows) = await Exec(MvRows, "events | mv-expand Tags | project Id");
		rows.Select(r => (long)r[0]!).Should().OnlyContain(id => id == 1L); // only the real array survives
	}

	[Fact]
	public async Task MvExpand_MultipleColumns_Throws()
	{
		var act = async () => await Exec(MvRows, "events | mv-expand Tags, Nums");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*exactly one*");
	}

	[Fact]
	public async Task MvExpand_ToTypeOf_Throws()
	{
		var act = async () => await Exec(MvRows, "events | mv-expand Tags to typeof(string)");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*typeof*");
	}

	// --- parse ---

	static readonly LogEntryRecord[] ParseRows =
	[
		Rec(1, LogLevel.Information, "user alice from eu", "svc"),
		Rec(2, LogLevel.Error, "no pattern here", "svc"),
		Rec(3, LogLevel.Warning, "user bob from us", "svc"),
	];

	[Fact]
	public async Task Parse_ExtractsCaptures_BetweenLiterals()
	{
		var (cols, rows) = await Exec(ParseRows, "events | parse Message with \"user \" User \" from \" Region | project Id, User, Region");
		cols.Select(c => c.Name).Should().ContainInOrder("Id", "User", "Region");
		cols[Col(cols, "User")].ClrType.Should().Be<string>();
		var byId = rows.ToDictionary(r => (long)r[0]!, r => ((string?)r[1], (string?)r[2]));
		byId[1L].Should().Be(("alice", "eu"));
		byId[3L].Should().Be(("bob", "us"));
	}

	[Fact]
	public async Task Parse_NonMatchingRow_IsRetainedWithNullCaptures()
	{
		var (_, rows) = await Exec(ParseRows, "events | parse Message with \"user \" User \" from \" Region | project Id, User");
		rows.Should().HaveCount(3); // the non-matching row is kept (Kusto `parse`, not `parse-where`)
		rows.Single(r => (long)r[0]! == 2L)[1].Should().BeNull();
	}

	[Fact]
	public async Task Parse_TrailingCapture_TakesRestOfString()
	{
		var (_, rows) = await Exec(ParseRows, "events | parse Message with \"user \" Rest | project Id, Rest");
		var byId = rows.ToDictionary(r => (long)r[0]!, r => (string?)r[1]);
		byId[1L].Should().Be("alice from eu");
		byId[2L].Should().BeNull(); // doesn't start with "user "
	}

	[Fact]
	public async Task Parse_FromPropertyBareName()
	{
		var rows = new[] { Rec(1, LogLevel.Information, "m", "svc", """{"Path":"/api/v1/users"}""") };
		var (cols, mat) = await Exec(rows, "events | parse Path with \"/api/\" Version \"/\" Resource | project Id, Version, Resource");
		mat[0][Col(cols, "Version")].Should().Be("v1");
		mat[0][Col(cols, "Resource")].Should().Be("users");
	}

	[Fact]
	public async Task Parse_TypedCapture_Throws()
	{
		var act = async () => await Exec(ParseRows, "events | parse Message with \"user \" User:string");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*typed capture*");
	}

	[Fact]
	public async Task Parse_RegexKind_Throws()
	{
		var act = async () => await Exec(ParseRows, "events | parse kind=regex Message with \"user \" User");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*simple*");
	}

	[Fact]
	public async Task Parse_NonStringSource_Throws()
	{
		var act = async () => await Exec(ParseRows, "events | parse Level with \"x\" A");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*string*");
	}
}
