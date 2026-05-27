using YobaBox.Core.Models;

namespace YobaBox.Config;

public sealed record ResolveMatch(ConfigBinding Binding, int Specificity);

public static class ResolvePipeline
{
	public static string? Resolve(string path, IReadOnlyList<string> requestTags, IReadOnlyList<ConfigBinding> bindings)
	{
		var match = ResolveDetailed(path, requestTags, bindings);
		return match?.Binding.Value;
	}

	public static ResolveMatch? ResolveDetailed(string path, IReadOnlyList<string> requestTags, IReadOnlyList<ConfigBinding> bindings)
	{
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(requestTags);
		ArgumentNullException.ThrowIfNull(bindings);

		var requestSet = new HashSet<string>(requestTags, StringComparer.OrdinalIgnoreCase);

		var candidates = bindings
			.Where(b => PathMatches(b.Path, path))
			.Select(b => (Binding: b, Tags: ParseTags(b.Tags)))
			.Where(c => c.Tags.IsSubsetOf(requestSet))
			.ToList();

		if (candidates.Count == 0)
			return null;

		var maxSpecificity = candidates.Max(c => c.Tags.Count);
		var winners = candidates.Where(c => c.Tags.Count == maxSpecificity).ToList();

		if (winners.Count > 1)
			throw new AmbiguousConfigException(path, winners.Select(w => w.Binding.Id).ToList());

		return new ResolveMatch(winners[0].Binding, maxSpecificity);
	}

	static bool PathMatches(string bindingPath, string requestPath) =>
		string.Equals(bindingPath, requestPath, StringComparison.OrdinalIgnoreCase);

	static HashSet<string> ParseTags(string tagString)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(tagString)) return set;
		foreach (var tag in tagString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			set.Add(tag);
		return set;
	}
}
