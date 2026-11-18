using Kusto.Language.Editor;

namespace PetBox.Log.Core.Query;

public static class KqlCompletionService
{
	// The ceiling on ONE answer, not the plan for it: see ApplyPerKindBudget. It exists because the
	// dropdown has to stay usable, and at a bare operand position the candidate set is 393 entries.
	public const int MaxItems = 50;

	// How many offers each KIND is guaranteed before any kind may spend the leftover slots. Sized to
	// cover every small kind in the surface as measured today (columns 10, prefix forms 7, keywords 4,
	// punctuation 5), so a family that teaches the language can never be crowded out by a catalog.
	public const int PerKindReserved = 10;

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

		var candidates = info.Items
			.Where(i => string.IsNullOrEmpty(prefix)
				|| i.MatchText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			.OrderBy(i => i.OrderText, StringComparer.Ordinal)
			.Select(ToCompletionItem)
			.OfType<KqlCompletionItem>()
			.ToList();

		return new KqlCompletionsResponse(editStart, editLength, ApplyPerKindBudget(candidates));
	}

	// The budget is per KIND, not a prefix of one alphabetical list. A single global Take let the
	// 369-entry built-in function catalog eat the whole answer: measured at `events | extend X = `, the
	// 50 slots were 10 columns + 35 functions + only the FIVE prefix forms that sort early enough,
	// which hid `real()` and `timespan()` — registered, compilable, and never shown. So every kind is
	// reserved its first PerKindReserved offers, then the leftover slots go to the same candidates in
	// the same alphabetical order the dropdown shows (a big catalog gets what is left, small families
	// get everything they have), and MaxItems still bounds the total. The FILTER, not this budget, is
	// what hides a construct the engine cannot compile — `dynamic()`, `toscalar` and `guid()` are gone
	// before a slot is handed out.
	static List<KqlCompletionItem> ApplyPerKindBudget(List<KqlCompletionItem> candidates)
	{
		var reserved = new bool[candidates.Count];
		var reservedPerKind = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < candidates.Count; i++)
		{
			reservedPerKind.TryGetValue(candidates[i].Kind, out var alreadyReserved);
			if (alreadyReserved >= PerKindReserved)
				continue;

			reservedPerKind[candidates[i].Kind] = alreadyReserved + 1;
			reserved[i] = true;
		}

		var kept = new bool[candidates.Count];
		var slots = MaxItems;

		// First pass: everyone's reservation.
		for (var i = 0; i < candidates.Count && slots > 0; i++)
		{
			if (!reserved[i])
				continue;

			kept[i] = true;
			slots--;
		}

		// Second pass: what is left of MaxItems, first candidate first in the shown order.
		for (var i = 0; i < candidates.Count && slots > 0; i++)
		{
			if (reserved[i])
				continue;

			kept[i] = true;
			slots--;
		}

		var answer = new List<KqlCompletionItem>(Math.Min(MaxItems, candidates.Count));
		for (var i = 0; i < candidates.Count; i++)
			if (kept[i])
				answer.Add(candidates[i]);

		return answer;
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
