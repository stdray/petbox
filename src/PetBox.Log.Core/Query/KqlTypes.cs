using System.Collections.Immutable;
using Kusto.Language;
using Kusto.Language.Symbols;

namespace PetBox.Log.Core.Query;

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

	// The Spans table root: the same KQL subset (whole operator catalog) over a named log's Spans table.
	// Start/End are wall-clock instants (datetime), Duration a timespan, Kind/Status carry both the numeric
	// code and a computed name form (KindName/StatusName — the span analog of Level/LevelName). Attributes
	// are addressed via the Properties dynamic bag (the AttributesJson analog of PropertiesJson).
	public static readonly TableSymbol Spans = new(
		KqlTransformer.SpansTable,
		new ColumnSymbol("SpanId", ScalarTypes.String),
		new ColumnSymbol("TraceId", ScalarTypes.String),
		new ColumnSymbol("ParentSpanId", ScalarTypes.String),
		new ColumnSymbol("Name", ScalarTypes.String),
		new ColumnSymbol("Kind", ScalarTypes.Int),
		new ColumnSymbol("KindName", ScalarTypes.String),
		new ColumnSymbol("Start", ScalarTypes.DateTime),
		new ColumnSymbol("End", ScalarTypes.DateTime),
		new ColumnSymbol("Duration", ScalarTypes.TimeSpan),
		new ColumnSymbol("Status", ScalarTypes.Int),
		new ColumnSymbol("StatusName", ScalarTypes.String),
		new ColumnSymbol("StatusDescription", ScalarTypes.String),
		new ColumnSymbol("Properties", ScalarTypes.Dynamic));

	public static readonly GlobalState Globals = GlobalState.Default
		.WithDatabase(new DatabaseSymbol(DatabaseName, Events, Spans));
}
