using YobaBox.Core.Models;

namespace YobaBox.Config;

public static class ResolvePipeline
{
	public static string? Resolve(string path, IReadOnlyList<string> requestTags, IReadOnlyList<ConfigBinding> bindings)
	{
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(requestTags);
		ArgumentNullException.ThrowIfNull(bindings);

		var requestSet = new HashSet<string>(requestTags, StringComparer.OrdinalIgnoreCase);

		var matches = bindings
			.Where(b => PathMatches(b.Path, path))
			.ToList();

		if (matches.Count == 0)
			return null;

		var best = matches
			.OrderByDescending(b => CountMatchingTags(b.Tags, requestSet))
			.ThenBy(b => b.Id)
			.First();

		return best.Value;
	}

	static bool PathMatches(string bindingPath, string requestPath) =>
		string.Equals(bindingPath, requestPath, StringComparison.OrdinalIgnoreCase);

	static int CountMatchingTags(string bindingTags, HashSet<string> requestSet)
	{
		if (string.IsNullOrWhiteSpace(bindingTags))
			return 0;

		return bindingTags
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Count(t => requestSet.Contains(t));
	}
}
