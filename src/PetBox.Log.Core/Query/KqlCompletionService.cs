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
		"extend",
		"count",
		"summarize",
		"sort",
		"order",
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
		if (i.Kind == CompletionKind.QueryPrefix && !SupportedQueryPrefixes.Contains(i.DisplayText))
			return null;

		if (i.DisplayText == "Properties")
			return new KqlCompletionItem("Column", "Properties", "Properties.", "");

		return new KqlCompletionItem(
			Kind: i.Kind.ToString(),
			DisplayText: i.DisplayText,
			BeforeText: i.BeforeText ?? "",
			AfterText: i.AfterText ?? "");
	}
}
