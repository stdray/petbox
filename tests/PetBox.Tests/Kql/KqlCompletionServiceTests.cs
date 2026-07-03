using PetBox.Log.Core.Query;

namespace PetBox.Tests.Kql;

public sealed class KqlCompletionServiceTests
{
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
		foreach (var op in new[] { "where", "take", "project", "extend", "count", "summarize", "sort", "order", "top", "distinct" })
			displays.Should().Contain(op, $"'{op}' is supported by KqlTransformer");
	}

	[Theory]
	[InlineData("join")]
	[InlineData("mv-expand")]
	[InlineData("mv-apply")]
	[InlineData("parse")]
	[InlineData("parse-kv")]
	[InlineData("parse-where")]
	[InlineData("evaluate")]
	[InlineData("top-hitters")]
	[InlineData("top-nested")]
	[InlineData("render")]
	[InlineData("serialize")]
	[InlineData("union")]
	[InlineData("lookup")]
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
}
