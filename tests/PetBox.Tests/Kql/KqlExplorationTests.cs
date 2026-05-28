using Kusto.Language;
using KustoLoco.Core;

namespace PetBox.Tests.Kql;

public sealed class KqlExplorationTests
{
	[Fact]
	public void KustoLanguage_Parse_NoDiagnostics_ForValidKql()
	{
		var code = KustoCode.Parse("events | where Level == 'Error' | take 10");
		var diagnostics = code.GetDiagnostics();

		diagnostics.Should().BeEmpty(
			"parser should not produce errors for a trivially valid query; got: {0}",
			string.Join("; ", diagnostics.Select(d => d.Message)));
	}

	[Fact]
	public void KustoLanguage_Parse_ReturnsDiagnostics_ForInvalid()
	{
		var code = KustoCode.Parse("events | completelymadeupoperator");
		var diagnostics = code.GetDiagnostics();

		diagnostics.Should().NotBeEmpty();
	}

	sealed record Row(long Id, DateTime Timestamp, string Level, string Message);

	[Fact]
	public async Task KustoLoco_RunQuery_InMemory_ReturnsExpectedRows()
	{
		var rows = new[]
		{
			new Row(1, new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc), "Information", "hello"),
			new Row(2, new DateTime(2026, 4, 19, 10, 1, 0, DateTimeKind.Utc), "Error", "boom"),
			new Row(3, new DateTime(2026, 4, 19, 10, 2, 0, DateTimeKind.Utc), "Warning", "meh"),
			new Row(4, new DateTime(2026, 4, 19, 10, 3, 0, DateTimeKind.Utc), "Error", "crash"),
		};

		var context = new KustoQueryContext();
		context.CopyDataIntoTable("events", rows);

		var result = await context.RunQuery("events | where Level == 'Error' | project Id, Message");

		result.Error.Should().BeNullOrEmpty();
		result.RowCount.Should().Be(2);
		var materialized = result.EnumerateRows().Select(r => r[1]?.ToString()).ToList();
		materialized.Should().BeEquivalentTo(["boom", "crash"]);
	}
}
