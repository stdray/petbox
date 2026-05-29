namespace PetBox.Core.Models;

// Canonical form for health-report tags: comma-separated "key:value" sorted by
// key (same flavour as config tags). Identity of a running thing = (Svc, canonical tags).
public static class HealthTags
{
	public static string Canonical(IReadOnlyDictionary<string, string>? tags)
	{
		if (tags is null || tags.Count == 0) return string.Empty;
		return string.Join(",", tags
			.Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
			.Select(kv => $"{kv.Key.Trim()}:{kv.Value?.Trim() ?? string.Empty}")
			.OrderBy(s => s, StringComparer.Ordinal));
	}

	public static IReadOnlyDictionary<string, string> Parse(string? canonical)
	{
		var dict = new Dictionary<string, string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(canonical)) return dict;
		foreach (var pair in canonical.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var i = pair.IndexOf(':');
			if (i <= 0) continue;
			dict[pair[..i].Trim()] = pair[(i + 1)..].Trim();
		}
		return dict;
	}

	public static string? Project(string? canonical) =>
		Parse(canonical).TryGetValue("project", out var p) ? p : null;
}
