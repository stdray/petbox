using System.Collections.Immutable;

namespace YobaBox.Log.Core.Query;

public sealed record KqlColumn(string Name, Type ClrType);

public sealed record KqlResult(IReadOnlyList<KqlColumn> Columns, IAsyncEnumerable<object?[]> Rows);

public sealed class UnsupportedKqlException(string message) : Exception(message);

public static class KqlSchema
{
	public const string DatabaseName = "yoba";
}
