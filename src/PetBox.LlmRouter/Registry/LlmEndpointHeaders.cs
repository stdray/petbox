using System.Text.Json;

namespace PetBox.LlmRouter.Registry;

// Storage codec for LlmEndpoint.Headers: a JSON object in the nullable llm_endpoints.Headers
// column. Null or empty on one side is null on the other, so "no headers" has ONE representation
// (NULL) and a round trip never turns an absent map into "{}". Keys are sorted on write so the
// stored text is deterministic and the level-content comparison does not see a spurious change.
public static class LlmEndpointHeaders
{
	public static string? Serialize(IReadOnlyDictionary<string, string>? headers) =>
		headers is not { Count: > 0 }
			? null
			: JsonSerializer.Serialize(new SortedDictionary<string, string>(
				headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal), StringComparer.Ordinal));

	// A corrupt cell resolves to "no headers" rather than throwing: this runs on the hot resolve path,
	// and a bad optional column must not take every route on the endpoint down with it.
	public static IReadOnlyDictionary<string, string>? Parse(string? json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
			return map is { Count: > 0 } ? map : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
