using System.Collections.Immutable;
using Kusto.Language;
using Kusto.Language.Symbols;

namespace YobaBox.Log.Core.Query;

public sealed record KqlColumn(string Name, Type ClrType);

public sealed record KqlResult(IReadOnlyList<KqlColumn> Columns, IAsyncEnumerable<object?[]> Rows);

public sealed class UnsupportedKqlException(string message) : Exception(message);

public static class KqlSchema
{
	public const string DatabaseName = "yoba";

	public static readonly TableSymbol Events = new(
		KqlTransformer.EventsTable,
		new ColumnSymbol("Id", ScalarTypes.Long),
		new ColumnSymbol("ServiceKey", ScalarTypes.String),
		new ColumnSymbol("Timestamp", ScalarTypes.DateTime),
		new ColumnSymbol("Level", ScalarTypes.Int),
		new ColumnSymbol("LevelName", ScalarTypes.String),
		new ColumnSymbol("Message", ScalarTypes.String),
		new ColumnSymbol("MessageTemplate", ScalarTypes.String),
		new ColumnSymbol("Exception", ScalarTypes.String),
		new ColumnSymbol("Properties", ScalarTypes.Dynamic));

	public static readonly GlobalState Globals = GlobalState.Default
		.WithDatabase(new DatabaseSymbol(DatabaseName, Events));
}
