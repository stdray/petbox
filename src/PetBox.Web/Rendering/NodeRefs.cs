using System.Text.RegularExpressions;

namespace PetBox.Web.Rendering;

// A resolved mention target handed to MarkdownRenderer — used by all three resolution maps
// (`[[slug]]` node refs, memory keys, `[[#comment]]` comment refs): the target's URL and its title
// (rendered as the link's `title` attribute). The renderer never touches the DB — a caller (a page
// model) resolves the mentions and builds this map, so the renderer stays a pure text transform.
// Title is null/empty when the target has none (then no title attribute is emitted).
//
// `Text` overrides the link's visible text, which otherwise stays the mention AS WRITTEN (the
// family default: a `[[slug]]` node ref reads as its slug even after a rename). It exists for
// comment refs, whose written form may be a 32-hex id — unreadable as anchor text — so
// CommentRefMap supplies "author · date" instead. Null = keep the mention's own text.
public sealed record NodeRefTarget(string Url, string? Title, string? Text = null);

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
