using System.Text.RegularExpressions;

namespace PetBox.Web.Rendering;

// A resolved `[[slug]]` mention target handed to MarkdownRenderer: the node's detail-page URL and
// its title (rendered as the link's `title` attribute). The renderer never touches the DB — a
// caller (a page model) resolves the slugs and builds this map, so the renderer stays a pure text
// transform. Title is null/empty when the node has none (then no title attribute is emitted).
public sealed record NodeRefTarget(string Url, string? Title);

// Mention-scanning helper for `[[slug]]` node references. The renderer applies the PRECISE
// exclusions (code spans/blocks, existing links); this cheap pre-scan over raw markdown just
// gathers candidate slugs so the page can batch-resolve them in one query. It's fine for the
// pre-scan to over-match a slug inside a code span — an extra resolved-but-unused map entry is
// harmless (the renderer won't link it there).
public static class NodeRefs
{
	// One flat-slug pattern shared with MarkdownRenderer.NodeRefRx: `[[` + a board-key-shaped slug
	// (a-z start, a-z0-9_- body, ≤100 chars, captured in group 1) + `]]`.
	public const string SlugPattern = @"\[\[([a-z][a-z0-9_-]{0,99})\]\]";

	static readonly Regex Rx = new(SlugPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// Distinct mention slugs found across the given raw markdown bodies (nulls/empties skipped).
	public static IReadOnlyCollection<string> ExtractSlugs(IEnumerable<string?> bodies)
	{
		var set = new HashSet<string>(StringComparer.Ordinal);
		foreach (var body in bodies)
		{
			if (string.IsNullOrEmpty(body)) continue;
			foreach (Match m in Rx.Matches(body))
				set.Add(m.Groups[1].Value);
		}
		return set;
	}
}
