using System.Text.Json;
using LinqToDB;
using LinqToDB.SqlQuery;

namespace PetBox.Log.Core.Query;

public static class KqlSqlExpressions
{
	[Sql.Expression("json_extract({0}, {1})", ServerSideOnly = false, IsNullable = Sql.IsNullableType.Nullable)]
	public static string? JsonExtract(string? json, string path) =>
		InMemoryJsonExtract(json, path);

	static string? InMemoryJsonExtract(string? json, string? path)
	{
		if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path) || !path.StartsWith("$.", StringComparison.Ordinal))
			return null;
		var key = path[2..];
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(key, out var prop))
				return null;
			return prop.ValueKind switch
			{
				JsonValueKind.String => prop.GetString(),
				JsonValueKind.Null or JsonValueKind.Undefined => null,
				_ => prop.GetRawText(),
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	[Sql.Function("json_extract", ServerSideOnly = true)]
	public static string? JsonExtractScalar(string? column, string path) => throw new NotSupportedException();
}

