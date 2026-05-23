namespace YobaBox.Log.Core.Query;

public sealed record KqlCompletionItem(
	string Kind,
	string DisplayText,
	string BeforeText,
	string AfterText);

public sealed record KqlCompletionsResponse(
	int EditStart,
	int EditLength,
	IReadOnlyList<KqlCompletionItem> Items);
