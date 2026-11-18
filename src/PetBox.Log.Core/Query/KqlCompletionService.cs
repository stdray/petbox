using Kusto.Language.Editor;

namespace PetBox.Log.Core.Query;

public static class KqlCompletionService
{
	public const int MaxItems = 50;

	static readonly HashSet<string> SupportedQueryPrefixes = new(StringComparer.Ordinal)
	{
		"where",
		"take",
		"limit",
		"project",
		// The column-selection family: they reshape the result in SQL (no in-memory fallback), so the
		// completion surface must offer them — hiding a working operator is the same defect as offering a
		// broken one.
		"project-away",
		"project-keep",
		"project-rename",
		"project-reorder",
		"project-by-names",
		"extend",
		"count",
		"summarize",
		"sort",
		"order",
		"top",
		"distinct",
		"join",
		"lookup",
		"mv-expand",
		"parse",
	};

	public static KqlCompletionsResponse Complete(string query, int position)
	{
		ArgumentNullException.ThrowIfNull(query);
		if (position < 0) position = 0;
		if (position > query.Length) position = query.Length;

		var svc = new KustoCodeService(query, KqlSchema.Globals);
		var info = svc.GetCompletionItems(position);

		var editStart = info.EditStart;
		var editLength = info.EditLength;
		var prefix = editLength > 0 && editStart >= 0 && editStart + editLength <= query.Length
			? query.Substring(editStart, editLength)
			: "";

		var filtered = info.Items
			.Where(i => string.IsNullOrEmpty(prefix)
				|| i.MatchText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.OrderBy(i => i.OrderText, StringComparer.Ordinal)
			.Select(ToCompletionItem)
			.Where(i => i is not null)
			.Select(i => i!)
			.Take(MaxItems)
			.ToList();

		return new KqlCompletionsResponse(editStart, editLength, filtered);
	}

	static KqlCompletionItem? ToCompletionItem(Kusto.Language.Editor.CompletionItem i)
	{
		if (!IsSupported(i))
			return null;

		if (i.DisplayText == "Properties")
			return new KqlCompletionItem("Column", "Properties", "Properties.", "");

		return new KqlCompletionItem(
			Kind: i.Kind.ToString(),
			DisplayText: i.DisplayText,
			BeforeText: i.BeforeText ?? "",
			AfterText: i.AfterText ?? "");
	}

	// The ONLY filter, and it asks the COMPILER, never a copy of its knowledge: a pipeline prefix against
	// the operator allowlist here; a scalar infix operator, a prefix form, a built-in function and an
	// aggregate function against KqlScalar, which in turn defers to the compiler's own data homes
	// (SupportedInfixOperators/SupportedScalarPrefixes, KqlScalarFunctions.Registry,
	// KqlTransformer.SupportedAggregates). An item the engine cannot compile must not be advertised —
	// offering `hasprefix`, or `toscalar`, before the compiler learned it was the reported defect.
	// Everything else (columns, tables, keywords, types) is offered unfiltered.
	static bool IsSupported(Kusto.Language.Editor.CompletionItem i) => i.Kind switch
	{
		CompletionKind.QueryPrefix => SupportedQueryPrefixes.Contains(i.DisplayText),
		CompletionKind.ScalarInfix => KqlScalar.CanCompileOperator(i.DisplayText),
		CompletionKind.ScalarPrefix => KqlScalar.CanCompileScalarPrefix(i.DisplayText),
		CompletionKind.BuiltInFunction => KqlScalar.CanCompileFunction(i.DisplayText),
		CompletionKind.AggregateFunction => KqlScalar.CanCompileAggregate(i.DisplayText),
		_ => true,
	};
}
