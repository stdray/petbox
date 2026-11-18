using Kusto.Language.Editor;

namespace PetBox.Tests.Kql;

public sealed class KqlCompletionServiceTests
{
	// --- the per-kind budget: a big catalog must not crowd a small family out of the answer ---

	// The prefix forms a bare operand position leads with. `real()` and `timespan()` sort behind the
	// built-in function catalog, and under the old single global Take(MaxItems) they were never shown —
	// registered, compilable, and invisible. That is the defect this file now pins, from the served side.
	static readonly string[] ScalarPrefixForms =
	[
		"datetime()", "decimal()", "double()", "int()", "long()", "real()", "timespan()",
	];

	[Theory]
	[InlineData("events | extend X = ")]
	[InlineData("events | where ")]
	[InlineData("events | summarize ")]
	public void BareOperandPosition_Offers_EveryScalarPrefixForm(string q)
	{
		var result = KqlCompletionService.Complete(q, q.Length);
		var served = result.Items
			.Where(i => i.Kind == nameof(CompletionKind.ScalarPrefix))
			.Select(i => i.DisplayText)
			.ToList();

		served.Should().BeEquivalentTo(ScalarPrefixForms,
			$"'{q}' sits where the whole prefix family is offered, and a per-kind budget keeps all of it "
			+ $"inside MaxItems = {KqlCompletionService.MaxItems}: a 369-entry function catalog must not "
			+ "decide which language constructs a user can discover");
	}

	[Fact]
	public void BareOperandPosition_IsStillBounded_AndStillFiltered()
	{
		const string q = "events | extend X = ";
		var result = KqlCompletionService.Complete(q, q.Length);

		// The budget reserves per kind, it does not remove the ceiling: the dropdown stays usable.
		result.Items.Should().HaveCountLessThanOrEqualTo(KqlCompletionService.MaxItems);

		// And the cap is not what hides an uncompilable construct — the filter is. These four are offered
		// by Kusto.Language at this exact position and are dropped before any slot is handed out.
		foreach (var dropped in new[] { "dynamic()", "dynamic", "toscalar", "guid()" })
			result.Items.Should().NotContain(i => i.DisplayText == dropped,
				$"'{dropped}' does not compile, so no budget may spend a slot on it");
	}

	// The invariant the previous branch established, checked at every position where the budget could
	// break it: every kind the service serves answers "can the engine compile this?" with yes. Narrowed
	// by a typed prefix too, because that is how a user reaches past any cap at all — and at the literal
	// `… | where Message has` Kusto.Language treats the operator as already typed (it hands back the
	// column/function catalog instead of the infix list), so the non-vacuity guard below sits one
	// keystroke earlier, where the operator list is still what is being offered.
	[Theory]
	[InlineData("events | where Message has")]
	[InlineData("events | where Message hassu")]
	[InlineData("events | where Id bet")]
	[InlineData("events | extend X = re")]
	[InlineData("events | summarize co")]
	[InlineData("events | extend X = ")]
	public void ServedItems_AreAllCompilable(string q)
	{
		var result = KqlCompletionService.Complete(q, q.Length);

		foreach (var item in result.Items)
			Compiles(item).Should().BeTrue($"'{item.Kind}' '{item.DisplayText}' was offered at '{q}', so it compiles");
	}

	[Fact]
	public void PrefixNarrowedQuery_Offers_CompilableOperatorsOnly()
	{
		const string q = "events | where Message hassu";
		var served = KqlCompletionService.Complete(q, q.Length).Items;

		served.Should().NotBeEmpty($"'{q}' is a half-typed operator, so the answer must not be empty")
			.And.Contain(i => i.DisplayText == "hassuffix")
			.And.AllSatisfy(i => Compiles(i).Should().BeTrue(
				$"'{i.DisplayText}' was offered at '{q}', so the engine compiles it"));
	}

	// The capability question, asked of the compiler's own homes (KqlScalar), never of a copy of its
	// knowledge. A kind that carries no capability claim — a column, a table, a keyword, punctuation —
	// is syntax and is offered unfiltered; QueryPrefix answers through the service's own allowlist, which
	// the AfterPipe tests below cover.
	static bool Compiles(KqlCompletionItem item) => item.Kind switch
	{
		nameof(CompletionKind.ScalarInfix) => KqlScalar.CanCompileOperator(item.DisplayText),
		nameof(CompletionKind.ScalarPrefix) => KqlScalar.CanCompileScalarPrefix(item.DisplayText),
		nameof(CompletionKind.BuiltInFunction) => KqlScalar.CanCompileFunction(item.DisplayText),
		nameof(CompletionKind.AggregateFunction) => KqlScalar.CanCompileAggregate(item.DisplayText),
		_ => true,
	};

	[Fact]
	public void ColumnPrefix_ReturnsMatchingColumns()
	{
		const string q = "events | where Le";
		var result = KqlCompletionService.Complete(q, q.Length);

		result.Items.Should().Contain(i => i.DisplayText == "Level");
		result.Items.Should().Contain(i => i.DisplayText == "LevelName");
		result.Items.Should().AllSatisfy(i => i.DisplayText.StartsWith("Le", StringComparison.Ordinal));
	}

	[Fact]
	public void TableName_ReturnsEvents()
	{
		const string q = "ev";
		var result = KqlCompletionService.Complete(q, q.Length);

		result.Items.Should().Contain(i => i.DisplayText == "events");
	}

	[Fact]
	public void EditRange_CoversPrefix()
	{
		const string q = "events | where Lev";
		var result = KqlCompletionService.Complete(q, q.Length);

		var prefix = q.Substring(result.EditStart, result.EditLength);
		prefix.Should().Be("Lev");
	}

	[Fact]
	public void NoItems_ReturnsEmpty()
	{
		const string q = "events | where ZzzzzNothingMatches";
		var result = KqlCompletionService.Complete(q, q.Length);
		result.Items.Should().BeEmpty();
	}

	[Fact]
	public void PositionOutOfRange_Clamped()
	{
		const string q = "events";
		var result = KqlCompletionService.Complete(q, q.Length * 10);
		result.Should().NotBeNull();
	}

	[Fact]
	public void MaxItems_RespectsCap()
	{
		var result = KqlCompletionService.Complete("", 0);
		result.Items.Should().HaveCountLessThanOrEqualTo(KqlCompletionService.MaxItems);
	}

	[Fact]
	public void AfterPipe_Offers_SupportedOperators()
	{
		const string q = "events | ";
		var result = KqlCompletionService.Complete(q, q.Length);

		var displays = result.Items.Select(i => i.DisplayText).ToHashSet(StringComparer.Ordinal);
		foreach (var op in new[]
			{ "where", "take", "project", "extend", "count", "summarize", "sort", "order", "top", "distinct",
			  "join", "lookup", "mv-expand", "parse" })
			displays.Should().Contain(op, $"'{op}' is supported by KqlTransformer");
	}

	[Theory]
	[InlineData("mv-apply")]
	[InlineData("parse-kv")]
	[InlineData("parse-where")]
	[InlineData("evaluate")]
	[InlineData("top-hitters")]
	[InlineData("top-nested")]
	[InlineData("render")]
	[InlineData("serialize")]
	[InlineData("union")]
	[InlineData("search")]
	[InlineData("scan")]
	[InlineData("make-series")]
	[InlineData("partition")]
	[InlineData("reduce")]
	[InlineData("sample")]
	[InlineData("as")]
	[InlineData("invoke")]
	[InlineData("getschema")]
	public void AfterPipe_Drops_Unsupported_QueryPrefixes(string unsupported)
	{
		const string q = "events | ";
		var result = KqlCompletionService.Complete(q, q.Length);

		result.Items.Should().NotContain(i => i.DisplayText == unsupported,
			$"'{unsupported}' is not supported by KqlTransformer → don't offer it");
	}

	[Fact]
	public void PropertiesColumn_InsertsDot_ForKeyLookupHandoff()
	{
		const string q = "events | where ";
		var result = KqlCompletionService.Complete(q, q.Length);

		var props = result.Items.FirstOrDefault(i => i.DisplayText == "Properties");
		props.Should().NotBeNull();
		props!.BeforeText.Should().Be("Properties.");
	}

	// --- the second table root: `spans` ---

	[Fact]
	public void TableName_ReturnsSpans()
	{
		const string q = "spa";
		var result = KqlCompletionService.Complete(q, q.Length);
		result.Items.Should().Contain(i => i.DisplayText == "spans");
	}

	[Fact]
	public void SpansRoot_OffersSpanColumns()
	{
		const string q = "spans | where ";
		var result = KqlCompletionService.Complete(q, q.Length);
		var displays = result.Items.Select(i => i.DisplayText).ToHashSet(StringComparer.Ordinal);
		foreach (var col in new[] { "TraceId", "SpanId", "Name", "Kind", "KindName", "Start", "End", "Duration", "Status" })
			displays.Should().Contain(col, $"'{col}' is a spans column");
	}

	[Fact]
	public void SpansRoot_ColumnPrefix_Filters()
	{
		const string q = "spans | where Tra";
		var result = KqlCompletionService.Complete(q, q.Length);
		result.Items.Should().Contain(i => i.DisplayText == "TraceId");
		result.Items.Should().AllSatisfy(i => i.DisplayText.StartsWith("Tra", StringComparison.Ordinal));
	}

	[Fact]
	public void SpansRoot_AfterPipe_Offers_SupportedOperators()
	{
		const string q = "spans | ";
		var result = KqlCompletionService.Complete(q, q.Length);
		var displays = result.Items.Select(i => i.DisplayText).ToHashSet(StringComparer.Ordinal);
		foreach (var op in new[] { "where", "project", "summarize", "top", "distinct", "join" })
			displays.Should().Contain(op, $"'{op}' is supported over the spans root");
	}
}
