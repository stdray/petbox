using Kusto.Language;
using Kusto.Language.Syntax;
using PetBox.Log.Core.Query;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Kql;

public sealed class KqlSyntaxKindAllowlistTests
{
	static readonly HashSet<SyntaxKind> SupportedOperators =
	[
		SyntaxKind.FilterOperator,
		SyntaxKind.TakeOperator,
		// SortOperator / TakeOperator also run post-shape (in-memory) after summarize/project/etc.
		SyntaxKind.SortOperator,
		SyntaxKind.ProjectOperator,
		SyntaxKind.CountOperator,
		SyntaxKind.SummarizeOperator,
		SyntaxKind.ExtendOperator,
		SyntaxKind.TopOperator,
		SyntaxKind.DistinctOperator,
	];

	static readonly HashSet<string> ExplicitlyUnsupportedNames = new(StringComparer.Ordinal)
	{
		"ConsumeOperator", "GraphWhereEdgesOperator", "GraphWhereNodesOperator",
		"MacroExpandOperator", "JoinOperator", "LookupOperator", "UnionOperator",
		"TopHittersOperator", "TopNestedOperator",
		"MvExpandOperator", "MvApplyOperator", "ParseOperator", "ParseWhereOperator",
		"ParseKvOperator", "EvaluateOperator", "ExecuteAndCacheOperator", "FacetOperator",
		"FindOperator", "SearchOperator", "SampleOperator", "SampleDistinctOperator",
		"ScanOperator", "SerializeOperator", "RenderOperator", "MakeSeriesOperator",
		"ReduceByOperator", "InvokeOperator", "AsOperator", "GetSchemaOperator",
		"PartitionOperator", "PartitionByOperator", "ProjectAwayOperator",
		"ProjectKeepOperator", "ProjectRenameOperator", "ProjectReorderOperator",
		"ProjectByNamesOperator", "RangeOperator", "PrintOperator", "AssertSchemaOperator",
		"ForkOperator", "BadQueryOperator", "GraphMarkComponentsOperator",
		"GraphMatchOperator", "GraphShortestPathsOperator", "GraphToTableOperator",
		"MakeGraphOperator",
	};

	[Fact]
	public void EverySyntaxKind_EndingInOperator_IsTriaged()
	{
		var allOperatorKinds = Enum.GetValues<SyntaxKind>()
			.Where(k => k.ToString().EndsWith("Operator", StringComparison.Ordinal))
			.ToHashSet();

		SupportedOperators.Should().BeSubsetOf(allOperatorKinds);

		var unsupportedAsKinds = allOperatorKinds
			.Where(k => ExplicitlyUnsupportedNames.Contains(k.ToString()))
			.ToHashSet();

		SupportedOperators.Intersect(unsupportedAsKinds).Should().BeEmpty();

		var untriaged = allOperatorKinds
			.Except(SupportedOperators)
			.Except(unsupportedAsKinds)
			.OrderBy(k => k.ToString(), StringComparer.Ordinal)
			.ToList();

		untriaged.Should().BeEmpty(
			"every pipeline operator must be triaged. New Kusto.Language version? Add each to " +
			"SupportedOperators (and wire KqlTransformer) or ExplicitlyUnsupportedNames. " +
			$"Untriaged: {string.Join(", ", untriaged)}");
	}

	[Fact]
	public void SupportedOperators_ActuallyParseWithoutUnsupportedKqlException()
	{
		var applyExamples = new Dictionary<SyntaxKind, string>
		{
			[SyntaxKind.FilterOperator] = "events | where Level >= 3",
			[SyntaxKind.TakeOperator] = "events | take 10",
			[SyntaxKind.SortOperator] = "events | order by Timestamp desc",
			[SyntaxKind.TopOperator] = "events | top 5 by Level desc",
		};
		var executeExamples = new Dictionary<SyntaxKind, string>
		{
			[SyntaxKind.ProjectOperator] = "events | project Level",
			[SyntaxKind.CountOperator] = "events | count",
			[SyntaxKind.SummarizeOperator] = "events | summarize count() by Level",
			[SyntaxKind.ExtendOperator] = "events | extend Doubled = Level * 2",
			[SyntaxKind.DistinctOperator] = "events | distinct Level",
		};

		SupportedOperators.Should().BeEquivalentTo(applyExamples.Keys.Concat(executeExamples.Keys));

		var source = Array.Empty<LogEntryRecord>().AsQueryable();

		foreach (var (kind, query) in applyExamples)
		{
			var code = ParseOrFail(query, kind);
			var act = () => KqlTransformer.Apply(source, code);
			act.Should().NotThrow<UnsupportedKqlException>(
				$"'{query}' ({kind}) must be handled by Apply");
		}
		foreach (var (kind, query) in executeExamples)
		{
			var code = ParseOrFail(query, kind);
			var act = () => KqlTransformer.Execute(source, code);
			act.Should().NotThrow<UnsupportedKqlException>(
				$"'{query}' ({kind}) must be handled by Execute");
		}

		static KustoCode ParseOrFail(string query, SyntaxKind kind)
		{
			var code = KustoCode.Parse(query);
			code.GetDiagnostics().Where(d => d.Severity == "Error").Should().BeEmpty(
				$"parse of '{query}' for {kind} should succeed");
			return code;
		}
	}

	[Fact]
	public void NoOperatorNamePatternChange_InKustoLanguage()
	{
		var operatorKinds = Enum.GetValues<SyntaxKind>()
			.Where(k => k.ToString().EndsWith("Operator", StringComparison.Ordinal))
			.ToList();
		operatorKinds.Should().HaveCountGreaterThan(20,
			"Kusto.Language historically exposes 30+ pipeline operators. " +
			"If this count collapses, the naming convention changed and the allowlist " +
			"enumeration is no longer accurate.");
	}
}
