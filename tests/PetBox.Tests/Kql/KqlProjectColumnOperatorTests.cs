using Kusto.Language;
using PetBox.Log.Core.Tracing;

namespace PetBox.Tests.Kql;

// The COLUMN SELECTION family: project-away / project-keep / project-by-names / project-rename /
// project-reorder. All five are pure SQL SELECT reshaping (no in-memory path), so every case here runs the
// whole pipeline through the real store on every backend in KqlBackendConfig.Active — a dialect that
// translates the emitted projection differently goes red here.
//
// The semantics pinned below come from the operator docs, not from intuition, and each one is a place where
// the intuitive reading is wrong:
//   * project-keep keeps SOURCE order, not argument order ("the order of the columns in the result is
//     determined by their original order in the table") — project-by-names is the one that follows the
//     specifier order, and the one that silently ignores names that match nothing.
//   * project-reorder orders COLUMNS, never ROWS. Its asc/desc/granny-* modifiers order the columns ONE
//     WILDCARD PATTERN matched ("how to order the columns when a wildcard pattern is used"); they are not a
//     row sort and not a position modifier for a single column.
//   * unmatched columns go LAST, in source order; a column matched by two specifiers lands at the FIRST.
//   * `Properties` is a VIRTUAL column (the dynamic bag): a column-selection list accepts it as an alias of
//     the stored PropertiesJson column that backs it, never as an emitted name.
// Every one of those has a case below.
public sealed class KqlProjectColumnOperatorTests
{
	static KustoCode Parse(string kql) => KustoCode.Parse(kql);

	// The unreshaped events shape, in order — the baseline every expectation is written against.
	static readonly string[] SourceColumns =
	[
		"Id", "ServiceKey", "Timestamp", "Level", "LevelName",
		"Message", "MessageTemplate", "Exception", "PropertiesJson",
	];

	static readonly LogEntryRecord[] Rows =
	[
		new() { Id = 1, TimestampMs = 100, Level = (int)LogLevel.Information, Message = "hello", MessageTemplate = "hello {}", ServiceKey = "svc-a", PropertiesJson = "{}" },
		new() { Id = 2, TimestampMs = 200, Level = (int)LogLevel.Error, Message = "boom", MessageTemplate = "boom {}", ServiceKey = "svc-b", PropertiesJson = "{}" },
		new() { Id = 3, TimestampMs = 300, Level = (int)LogLevel.Warning, Message = "meh", MessageTemplate = "meh {}", ServiceKey = "svc-a", PropertiesJson = "{}" },
		new() { Id = 4, TimestampMs = 400, Level = (int)LogLevel.Error, Message = "crash", MessageTemplate = "crash {}", ServiceKey = "svc-c", PropertiesJson = "{}" },
	];

	static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> RunAsync(string kql, KqlBackend backend) =>
		KqlTestHost.ExecuteAsync(Rows, Parse(kql), backend);

	// Rows whose bag carries REAL keys, so a Properties["k"] lookup has something to find (Rows' bag is {}).
	static readonly LogEntryRecord[] BagRows =
	[
		new() { Id = 1, TimestampMs = 100, Level = (int)LogLevel.Information, Message = "hello", ServiceKey = "svc-a", PropertiesJson = """{"city":"Paris","Tool":"curl"}""" },
		new() { Id = 2, TimestampMs = 200, Level = (int)LogLevel.Error, Message = "boom", ServiceKey = "svc-b", PropertiesJson = "{}" },
	];

	static Task<(IReadOnlyList<KqlColumn> Columns, List<object?[]> Rows)> RunBagAsync(string kql, KqlBackend backend) =>
		KqlTestHost.ExecuteAsync(BagRows, Parse(kql), backend);

	// The spans root carries the same two computed name columns plus the same streamed bag name.
	static readonly SpanRecord[] SpanRows =
	[
		new() { SpanId = "s1", TraceId = "t1", Name = "op", Kind = 1, StatusCode = 2, StartUnixNs = 1_000_000, EndUnixNs = 6_000_000, AttributesJson = """{"k":"v"}""" },
	];

	static async Task AssertSpanColumnsAsync(string kql, params string[] expected)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, _) = await KqlTestHost.ExecuteSpansAsync(SpanRows, Parse(kql), backend);
			cols.Select(c => c.Name).Should().Equal(expected, $"'{kql}' on {backend}");
		}
	}

	static async Task AssertColumnsAsync(string kql, params string[] expected)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, _) = await RunAsync(kql, backend);
			cols.Select(c => c.Name).Should().Equal(expected, $"'{kql}' on {backend}");
		}
	}

	static async Task AssertThrowsAsync(string kql, string messageFragment)
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var act = async () => await RunAsync(kql, backend);
			(await act.Should().ThrowAsync<UnsupportedKqlException>())
				.WithMessage($"*{messageFragment}*", $"'{kql}' on {backend} must be refused with a precise error");
		}
	}

	// Id is column 0 in every baseline shape; a reshaped query reads its values by index.
	static object? ValueOf(List<object?[]> rows, long id, int columnIndex) =>
		rows.Single(r => (long)r[0]! == id)[columnIndex];

	// ---- project-away: every column EXCEPT the named ones, in source order ----

	[Fact]
	public Task ProjectAway_DropsNamedColumns_KeepingSourceOrder() =>
		AssertColumnsAsync("events | project-away Message, Exception",
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName", "MessageTemplate", "PropertiesJson");

	[Fact]
	public async Task ProjectAway_KeepsTheValuesOfWhatIsLeft()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-away Message, Exception", backend);
			rows.Should().HaveCount(4, $"on {backend}");
			ValueOf(rows, 2, 1).Should().Be("svc-b");       // ServiceKey survived the drop
			ValueOf(rows, 2, 3).Should().Be((int)LogLevel.Error); // Level too
		}
	}

	[Theory]
	[InlineData("events | project-away *essage*", // '*' as a prefix and a suffix, mid-name
		new[] { "Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Exception", "PropertiesJson" })]
	[InlineData("events | project-away levelname", // case-insensitive, like every bare column ref
		new[] { "Id", "ServiceKey", "Timestamp", "Level", "Message", "MessageTemplate", "Exception", "PropertiesJson" })]
	[InlineData("events | project-away *e*", // matches every column carrying an 'e'
		new[] { "Id" })]
	public Task ProjectAway_Wildcards(string kql, string[] expected) => AssertColumnsAsync(kql, expected);

	// A pattern is a SET, and an empty set is a legitimate result: `Nope*` drops nothing. A plain name
	// that does not exist is a typo and IS an error — see the next case.
	[Fact]
	public Task ProjectAway_PatternMatchingNothing_IsNotAnError() => AssertColumnsAsync("events | project-away Nope*", SourceColumns);

	[Fact]
	public Task ProjectAway_UnknownColumn_ThrowsPrecisely() =>
		AssertThrowsAsync("events | project-away Nope", "project-away: unknown column 'Nope'");

	[Fact]
	public async Task ProjectAway_ThenWhere_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-away Message | where Level >= 4", backend);
			cols.Select(c => c.Name).Should().Equal(
				["Id", "ServiceKey", "Timestamp", "Level", "LevelName", "MessageTemplate", "Exception", "PropertiesJson"],
				$"on {backend}");
			rows.Select(r => (long)r[0]!).Should().BeEquivalentTo([2L, 4L], $"on {backend}");
		}
	}

	// A dropped column is no longer a COLUMN, so a later stage resolves it the way any unknown bare name is
	// resolved while the shape still carries the JSON bag: a Properties.<name> lookup (which matches nothing
	// here). The precise unknown-column error is what the next case pins — it appears once the bag itself is
	// gone, exactly as it does after a `project` that dropped PropertiesJson.
	[Fact]
	public async Task ProjectAway_DroppedColumn_DegradesToAPropertiesLookup()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-away Message | where Message == 'boom'", backend);
			rows.Should().BeEmpty($"the dropped column is a Properties lookup now, so it matches nothing on {backend}");
		}
	}

	[Fact]
	public Task ProjectKeep_WithoutTheBag_LeavesNoFallback_SoTheErrorIsPrecise() =>
		AssertThrowsAsync("events | project-keep Id, Level | where Message == 'boom'", "unknown column 'Message'");

	[Fact]
	public async Task ProjectAway_ThenSummarize_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-away Message | summarize count() by Level", backend);
			cols.Select(c => c.Name).Should().Equal(["Level", "count_"], $"on {backend}");
			rows.Select(r => ((int)r[0]!, (long)r[1]!)).Should().BeEquivalentTo(
				[((int)LogLevel.Information, 1L), ((int)LogLevel.Error, 2L), ((int)LogLevel.Warning, 1L)],
				$"on {backend}");
		}
	}

	// ---- project-keep: exactly the named columns, in SOURCE order (not argument order) ----

	[Fact]
	public Task ProjectKeep_KeepsOnlyNamedColumns_InSourceOrder() =>
		AssertColumnsAsync("events | project-keep ServiceKey, Id", "Id", "ServiceKey");

	[Fact]
	public async Task ProjectKeep_KeepsTheValuesOfWhatIsKept()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-keep Id, ServiceKey", backend);
			ValueOf(rows, 1, 1).Should().Be("svc-a", $"on {backend}");
		}
	}

	[Theory]
	[InlineData("events | project-keep Message*, Id", new[] { "Id", "Message", "MessageTemplate" })]
	[InlineData("events | project-keep *NAME", new[] { "LevelName" })] // case-insensitive pattern
	public Task ProjectKeep_Wildcards(string kql, string[] expected) => AssertColumnsAsync(kql, expected);

	[Fact]
	public Task ProjectKeep_UnknownColumn_ThrowsPrecisely() =>
		AssertThrowsAsync("events | project-keep Id, Nope", "project-keep: unknown column 'Nope'");

	[Fact]
	public async Task ProjectKeep_ThenWhere_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync(
				"events | project-keep Id, Level, ServiceKey | where Level >= 3 | order by Id asc", backend);
			// Source order, not the argument order: Id, ServiceKey, Timestamp, Level, … keeps Id, ServiceKey, Level.
			cols.Select(c => c.Name).Should().Equal(["Id", "ServiceKey", "Level"], $"on {backend}");
			rows.Select(r => (long)r[0]!).Should().Equal([2L, 3L, 4L], $"on {backend} — column order is source order, row order is the where");
		}
	}

	[Fact]
	public async Task ProjectKeep_ThenSummarize_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-keep Level | summarize count() by Level", backend);
			cols.Select(c => c.Name).Should().Equal(["Level", "count_"], $"on {backend}");
			rows.Should().HaveCount(3, $"on {backend}");
		}
	}

	// ---- project-rename: same columns, same order, relabelled in place ----

	[Fact]
	public Task ProjectRename_RelabelsInPlace() =>
		AssertColumnsAsync("events | project-rename Text = Message, Svc = ServiceKey",
			"Id", "Svc", "Timestamp", "Level", "LevelName", "Text", "MessageTemplate", "Exception", "PropertiesJson");

	[Fact]
	public async Task ProjectRename_KeepsTheRenamedColumnsValues()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-rename Text = Message, Svc = ServiceKey", backend);
			ValueOf(rows, 1, 1).Should().Be("svc-a", $"on {backend}");
			ValueOf(rows, 1, 5).Should().Be("hello", $"on {backend}");
		}
	}

	[Fact]
	public Task ProjectRename_UnknownColumn_ThrowsPrecisely() =>
		AssertThrowsAsync("events | project-rename Text = Nope", "project-rename: unknown column 'Nope'");

	// Two columns under one name is a shape no downstream stage could address, so the rename is refused
	// rather than emitting a silent duplicate.
	[Fact]
	public Task ProjectRename_OntoAnExistingColumn_ThrowsPrecisely() =>
		AssertThrowsAsync("events | project-rename Level = Message", "project-rename: column 'Level' already exists");

	// The classic misuse: project-rename only renames, it does not compute ('project' is the operator for
	// that), so the error says so instead of half-supporting the expression.
	[Fact]
	public Task ProjectRename_ComputedExpression_IsRefusedPrecisely() =>
		AssertThrowsAsync("events | project-rename Doubled = Level * 2", "not a column name — project-rename only renames");

	[Fact]
	public async Task ProjectRename_ThenWhere_UsesTheNewName()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-rename Text = Message | where Text == 'boom'", backend);
			rows.Select(r => (long)r[0]!).Should().Equal([2L], $"on {backend}");
		}
	}

	[Fact]
	public async Task ProjectRename_OldName_DegradesToAPropertiesLookup()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-rename Text = Message | where Message == 'boom'", backend);
			rows.Should().BeEmpty($"the old name is an unknown bare name now — a Properties lookup on {backend}");
		}
	}

	[Fact]
	public async Task ProjectRename_ThenSummarize_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-rename Sev = Level | summarize count() by Sev", backend);
			cols.Select(c => c.Name).Should().Equal(["Sev", "count_"], $"on {backend}");
			rows.Select(r => ((int)r[0]!, (long)r[1]!)).Should().Contain(
				((int)LogLevel.Error, 2L), $"on {backend}");
		}
	}

	// ---- project-reorder: COLUMN order only — never row order ----

	[Fact]
	public Task ProjectReorder_MovesNamedColumnsToTheFront_AndAppendsTheRestInSourceOrder() =>
		AssertColumnsAsync("events | project-reorder Message, Id",
			"Message", "Id", "ServiceKey", "Timestamp", "Level", "LevelName", "MessageTemplate", "Exception", "PropertiesJson");

	// THE pin on the classic misreading: `order by Id asc` fixes the ROW order, and project-reorder must
	// leave it alone — the Id column moves to position 1 and still reads 1,2,3,4.
	[Fact]
	public async Task ProjectReorder_DoesNotSortRows()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | order by Id asc | project-reorder Message, Id", backend);
			cols.Select(c => c.Name).Take(2).Should().Equal(["Message", "Id"], $"on {backend}");
			rows.Select(r => (long)r[1]!).Should().Equal([1L, 2L, 3L, 4L],
				$"project-reorder reorders COLUMNS; the row order from `order by` must survive on {backend}");
		}
	}

	[Fact]
	public async Task ProjectReorder_KeepsEveryValueUnderItsColumn()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunAsync("events | project-reorder Level, Id, Message", backend);
			// Id has MOVED to index 1, so the row is located by it there — the values must travel with
			// their column, not stay behind in the old position.
			var row = rows.Single(r => (long)r[1]! == 2);
			row[0].Should().Be((int)LogLevel.Error, $"on {backend}");
			row[2].Should().Be("boom", $"on {backend}");
		}
	}

	// Without a modifier a pattern keeps SOURCE order (doc: "if no explicit ordering is specified, the order
	// is determined by the matching columns as they appear in the source table").
	[Fact]
	public Task ProjectReorder_WildcardWithoutModifier_KeepsSourceOrder() =>
		AssertColumnsAsync("events | project-reorder Message*, Id",
			"Message", "MessageTemplate", "Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Exception", "PropertiesJson");

	// With one, the MATCHED SET is ordered by name — desc here flips the two Message* columns.
	[Fact]
	public Task ProjectReorder_WildcardWithDesc_OrdersTheMatchedSetByName() =>
		AssertColumnsAsync("events | project-reorder Message* desc, Id",
			"MessageTemplate", "Message", "Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Exception", "PropertiesJson");

	[Fact]
	public Task ProjectReorder_StarWithGrannyAsc_SortsEveryColumn() =>
		AssertColumnsAsync("events | project-reorder * granny-asc",
			"Exception", "Id", "Level", "LevelName", "Message", "MessageTemplate", "PropertiesJson", "ServiceKey", "Timestamp");

	// "granny" = natural order: a run of digits compares by VALUE (Kusto's own a20-before-a100 example).
	// No table in this schema has numeric column names, so the rule is pinned on the comparator itself.
	[Fact]
	public void NaturalCompare_OrdersDigitRunsByValue()
	{
		KqlTransformer.NaturalCompare("a20", "a100").Should().BeNegative();
		KqlTransformer.NaturalCompare("a100", "a20").Should().BePositive();
		KqlTransformer.NaturalCompare("a9", "a20").Should().BeNegative();
		KqlTransformer.NaturalCompare("a007", "a7").Should().Be(0);
		KqlTransformer.NaturalCompare("A20", "a20").Should().Be(0); // case-insensitive, like `asc`
	}

	[Fact]
	public Task ProjectReorder_UnknownColumn_ThrowsPrecisely() =>
		AssertThrowsAsync("events | project-reorder Level, Nope", "project-reorder: unknown column 'Nope'");

	[Fact]
	public async Task ProjectReorder_ThenWhere_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-reorder Level, Id | where Level >= 4 | order by Id asc", backend);
			cols.Select(c => c.Name).Take(2).Should().Equal(["Level", "Id"], $"on {backend}");
			rows.Select(r => (long)r[1]!).Should().Equal([2L, 4L], $"on {backend}");
		}
	}

	[Fact]
	public async Task ProjectReorder_ThenSummarize_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-reorder Level, Message | summarize count() by Level", backend);
			cols.Select(c => c.Name).Should().Equal(["Level", "count_"], $"on {backend}");
			rows.Should().HaveCount(3, $"on {backend}");
		}
	}

	// ---- project-by-names: same selection, SPECIFIER order, unmatched names ignored ----

	[Fact]
	public Task ProjectByNames_FollowsSpecifierOrder() =>
		AssertColumnsAsync("events | project-by-names ServiceKey, Id", "ServiceKey", "Id");

	// The documented difference from project-keep, verbatim: "column names that don't match any existing
	// column are safely ignored".
	[Fact]
	public Task ProjectByNames_UnmatchedNamesAreIgnored() =>
		AssertColumnsAsync("events | project-by-names Nope, Id, AlsoNope", "Id");

	// NOTE: this Kusto.Language (12.4.1) does not yet let a wildcard into project-by-names' grammar
	// (`project-by-names Message*` is a PARSE error), so no pattern case here. The compiler admits one
	// (MatchColumnElement), it is just unreachable from this parser version.

	[Fact]
	public async Task ProjectByNames_ThenWhere_Composes()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunAsync("events | project-by-names Message, Id | where Id >= 3", backend);
			cols.Select(c => c.Name).Should().Equal(["Message", "Id"], $"on {backend}");
			rows.Select(r => (long)r[1]!).Should().BeEquivalentTo([3L, 4L], $"on {backend}");
		}
	}

	// ---- the VIRTUAL `Properties` column: the bag is STORED as PropertiesJson ----
	//
	// KqlSchema advertises `Properties` (completion offers it, `where Properties["k"] == …` works), but the row
	// shape carries it as PropertiesJson — so before this rule the two operators whose whole job is column
	// membership were the only places the advertised column did not exist, on exactly the query a user writes
	// to get rid of the giant JSON blob. The rule: a column-selection list accepts `Properties` as an ALIAS of
	// the backing column and never emits it under the alias.

	// Measured on the deployed build (ffdda0b) as "unknown column 'Properties'" — red before the fix.
	[Fact]
	public Task ProjectAway_Properties_DropsTheBag() =>
		AssertColumnsAsync("events | project-away Properties",
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Message", "MessageTemplate", "Exception");

	[Fact]
	public Task ProjectAway_Properties_IsCaseInsensitive() =>
		AssertColumnsAsync("events | project-away properties",
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Message", "MessageTemplate", "Exception");

	// The alias must not be a second, subtly different operator: same columns out, same error afterwards.
	[Fact]
	public async Task ProjectAway_Properties_AndPropertiesJson_SelectTheSameColumns()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (aliasCols, aliasRows) = await RunBagAsync("events | project-away Properties", backend);
			var (storedCols, storedRows) = await RunBagAsync("events | project-away PropertiesJson", backend);
			aliasCols.Select(c => c.Name).Should().Equal(storedCols.Select(c => c.Name).ToArray(), $"on {backend}");
			aliasRows.Should().HaveCount(storedRows.Count, $"on {backend}");
		}
	}

	// The bag stays under its STORED name — emitting the column as `Properties` would be a trap, because the
	// downstream bag lookup keys on the PropertiesJson name (see the addressability case below).
	[Fact]
	public async Task ProjectKeep_Properties_KeepsTheBag_UnderItsStoredName()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunBagAsync("events | project-keep Id, Properties", backend);
			cols.Select(c => c.Name).Should().Equal(["Id", "PropertiesJson"], $"on {backend}");
			rows.Single(r => (long)r[0]! == 1)[1].Should()
				.Be("""{"city":"Paris","Tool":"curl"}""", $"the kept bag is the stored JSON text on {backend}");
		}
	}

	// ...and the keep that kept the bag must not be the thing that breaks Properties["k"].
	[Fact]
	public async Task ProjectKeep_Properties_LeavesTheBagAddressable()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (_, rows) = await RunBagAsync(
				"events | project-keep Id, Properties | where Properties[\"city\"] == \"Paris\"", backend);
			rows.Select(r => (long)r[0]!).Should().Equal([1L], $"on {backend}");
		}
	}

	// THE pinned after-away rule: dropping the bag drops the LOOKUPS with it, with the SAME precise error the
	// stored spelling has always produced — never a null map, never a silent empty bag, never a fallback.
	[Theory]
	[InlineData("events | project-away Properties")]
	[InlineData("events | project-away PropertiesJson")]
	public Task ProjectAway_Bag_DropsTheBagLookupsToo(string kql) =>
		AssertThrowsAsync(kql + " | where Properties[\"Tool\"] != \"\"", "input has no PropertiesJson column");

	// The rest of the family speaks the same alias (one resolution rule, five operators).
	[Fact]
	public Task ProjectByNames_Properties_UsesItsStoredName() =>
		AssertColumnsAsync("events | project-by-names Properties, Id", "PropertiesJson", "Id");

	[Fact]
	public Task ProjectReorder_Properties_MovesTheBag() =>
		AssertColumnsAsync("events | project-reorder Properties, Id",
			"PropertiesJson", "Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Message", "MessageTemplate", "Exception");

	[Fact]
	public Task ProjectRename_Properties_RenamesTheBag() =>
		AssertColumnsAsync("events | project-rename P = Properties",
			"Id", "ServiceKey", "Timestamp", "Level", "LevelName", "Message", "MessageTemplate", "Exception", "P");

	// ---- the OTHER pseudo columns: the inventory that says Properties was the only hole ----
	//
	// LevelName, Exception and PropertiesJson are REAL columns of the events shape (the name form of Level,
	// the exception text, the stored bag); KindName/StatusName are the same on spans. None of them is virtual,
	// so away/keep have always seen them — which is exactly why the hole was `Properties` alone.
	[Theory]
	[InlineData("events | project-away LevelName, Exception, PropertiesJson",
		new[] { "Id", "ServiceKey", "Timestamp", "Level", "Message", "MessageTemplate" })]
	[InlineData("events | project-keep LevelName, Exception, PropertiesJson",
		new[] { "LevelName", "Exception", "PropertiesJson" })]
	public Task PseudoColumns_Events_AreRealColumns(string kql, string[] expected) => AssertColumnsAsync(kql, expected);

	[Fact]
	public Task PseudoColumns_Spans_AreRealColumns() =>
		AssertSpanColumnsAsync("spans | project-keep KindName, StatusName, PropertiesJson",
			"KindName", "StatusName", "PropertiesJson");

	[Fact]
	public Task PseudoColumns_Spans_AreRemovableToo() =>
		AssertSpanColumnsAsync("spans | project-away KindName, StatusName, Properties",
			"SpanId", "TraceId", "ParentSpanId", "Name", "Kind", "Start", "End", "Duration", "Status", "StatusDescription");

	// ---- the `project` side of the same name, pinned so the divergence is on the record ----
	//
	// `project Properties` goes through the bare-name Properties fallback, so it yields the bag KEY named
	// "Properties" — a nullable string column, null for every row here — NOT the bag. That is why the
	// column-selection family aliases it to the stored column instead of copying `project`. The documented
	// projection form is `Name = Properties["key"]`, which works (and matches Kusto).
	[Fact]
	public async Task Project_BareProperties_IsTheBagKeyFallback_NotTheBag()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunBagAsync("events | project Properties", backend);
			cols.Select(c => c.Name).Should().Equal(["Properties"], $"on {backend}");
			cols[0].ClrType.Should().Be<string>($"on {backend}");
			rows.Should().OnlyContain(r => r[0] == null, $"it is the null bag key 'Properties', not the bag — on {backend}");
		}
	}

	[Fact]
	public async Task Project_BagAccess_WorksThroughTheNamedForm()
	{
		foreach (var backend in KqlBackendConfig.Active)
		{
			var (cols, rows) = await RunBagAsync("events | project City = Properties[\"city\"]", backend);
			cols.Select(c => c.Name).Should().Equal(["City"], $"on {backend}");
			rows.Select(r => (string?)r[0]).Should().Contain("Paris", $"on {backend}");
		}
	}

	// ---- wildcard semantics (the matcher itself, so the '*' rules are pinned where they are decided) ----

	[Theory]
	[InlineData("b*", "b", true)] // '*' matches the EMPTY run
	[InlineData("b*", "boom", true)]
	[InlineData("*", "anything", true)]
	[InlineData("*e*", "Message", true)]
	[InlineData("*e*e*", "Message", true)] // several stars
	[InlineData("Mes**age", "Message", true)] // adjacent stars collapse
	[InlineData("Mes*age", "Message", true)] // star in the MIDDLE
	[InlineData("*essage", "Message", true)]
	[InlineData("message", "Message", true)] // case-insensitive
	[InlineData("*z*", "Message", false)]
	[InlineData("z*", "Message", false)]
	[InlineData("*z", "Message", false)]
	[InlineData("Message*X", "Message", false)]
	public void ColumnWildcard_MatchesAnyStarRun(string pattern, string name, bool expected) =>
		KqlTransformer.ColumnPatternMatches(pattern, name).Should().Be(expected,
			$"'{pattern}' vs '{name}': '*' is the only wildcard and matches any run, zero or more");

	// ---- the live-tail surface: refused, never silently full-scanned ----

	// These five reshape the row, so the live-tail filter (which has a per-ROW form only) must keep refusing
	// them — the page hides the toggle via IsShapeChanged and LogApi answers 400. A full scan is NOT the
	// fallback: "the live tail of a reshaped row" is not a stream.
	[Theory]
	[InlineData("events | project-away Message")]
	[InlineData("events | project-keep Id, Level")]
	[InlineData("events | project-rename Text = Message")]
	[InlineData("events | project-reorder Level")]
	[InlineData("events | project-by-names Id, Level")]
	public void ColumnSelectionOperators_AreShapeChanging_ForTheLiveTail(string kql) =>
		KqlTransformer.HasShapeChangingOps(Parse(kql)).Should().BeTrue(
			$"'{kql}' changes the row shape, so live tail must refuse it (400), not stream it unfiltered");

	// The inverse also has to hold, or the toggle disappears for queries that DO have a per-row form.
	[Theory]
	[InlineData("events | where Level >= 3")]
	[InlineData("events | order by Timestamp desc | take 5")]
	public void RowLevelQueries_AreNotShapeChanging(string kql) =>
		KqlTransformer.HasShapeChangingOps(Parse(kql)).Should().BeFalse();

	// ---- completion surface: a working operator must be offered ----

	// The inverse of the defect the completion card fixed: an operator the engine compiles must not be filtered
	// out of the menu (that is how `mv-expand`/`parse` used to be hidden while working). project-by-names is
	// NOT asserted here — Kusto.Language 12.4.1's completion service emits no QueryPrefix item for it, so it is
	// kept on KqlCompletionService's allowlist (the filter must not be the thing that hides it) but there is
	// nothing to offer until the compiler lists it.
	[Fact]
	public void Completion_OffersTheColumnSelectionOperators()
	{
		const string q = "events | ";
		var displays = KqlCompletionService.Complete(q, q.Length).Items
			.Select(i => i.DisplayText).ToHashSet(StringComparer.Ordinal);
		foreach (var op in new[] { "project-away", "project-keep", "project-rename", "project-reorder" })
			displays.Should().Contain(op, $"'{op}' is supported by KqlTransformer, so completion must offer it");
	}
}
