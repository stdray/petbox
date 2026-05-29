using PetBox.Core.Models;

namespace PetBox.Config;

public sealed record ResolveMatch(ConfigBinding Binding, int Specificity);

public static class ResolvePipeline
{
	public static string? Resolve(string path, IReadOnlyList<string> requestTags, IReadOnlyList<ConfigBinding> bindings)
	{
		var match = ResolveDetailed(path, requestTags, bindings);
		return match?.Binding.Value;
	}

	// Resolves every distinct path present in the binding set against the request tag-vector.
	// Used by the bulk /v1/conf endpoint. Throws AmbiguousConfigException for the first path
	// that has competing equally-specific bindings — surfaced to the caller as 409 (we fail
	// loudly rather than silently dropping a misconfigured key).
	public static IReadOnlyList<ResolveMatch> ResolveAll(IReadOnlyList<string> requestTags, IReadOnlyList<ConfigBinding> bindings)
	{
		ArgumentNullException.ThrowIfNull(requestTags);
		ArgumentNullException.ThrowIfNull(bindings);

		var matches = new List<ResolveMatch>();
		foreach (var path in bindings.Where(b => !b.IsDeleted).Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			var match = ResolveDetailed(path, requestTags, bindings);
			if (match is not null)
				matches.Add(match);
		}
		return matches;
	}

	public static ResolveMatch? ResolveDetailed(string path, IReadOnlyList<string> requestTags, IReadOnlyList<ConfigBinding> bindings)
	{
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(requestTags);
		ArgumentNullException.ThrowIfNull(bindings);

		var requestSet = new HashSet<string>(requestTags, StringComparer.OrdinalIgnoreCase);

		var candidates = bindings
			.Where(b => !b.IsDeleted && PathMatches(b.Path, path))
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
