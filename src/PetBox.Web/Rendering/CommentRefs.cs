using System.Text.RegularExpressions;

namespace PetBox.Web.Rendering;

// Mention-scanning helper for `[[#comment]]` COMMENT references (work `comment-slug-and-refs`,
// spec `comment-ref-links`) — the fourth member of the autolink family, sibling of NodeRefs and
// MemoryRefs, and built the same way for the same reason: the renderer applies the precise
// exclusions (code spans/blocks, existing links); this cheap pre-scan over raw markdown just
// gathers candidate tokens so the page can decide, ONCE, which of them it is willing to resolve.
//
// The `#` is what keeps this from colliding with a `[[slug]]` node mention: NodeRefs.SlugPattern
// requires `[[` to be followed immediately by a-z, so `[[#…]]` can never be read as a node ref, and
// a node ref can never be read as a comment ref. One family, two disjoint shapes.
//
// A token is EITHER a comment's slug (the flat-slug shape a node key has) OR its 32-hex id — which
// is why the pattern below is wider than either: it accepts any flat identifier and lets the
// RESOLUTION MAP decide. Over-matching costs nothing here (an unmapped token stays literal, and an
// unused map entry never becomes a link), and it means the renderer never has to know which of the
// two address forms it is looking at.
public static class CommentRefs
{
	// One pattern shared with MarkdownRenderer.CommentRefRx: `[[#` + a flat identifier (alnum
	// start — a slug starts a-z, a 32-hex id may start with a digit — then a-z0-9_-, ≤100 chars,
	// captured in group 1) + `]]`.
	public const string TokenPattern = @"\[\[#([a-zA-Z0-9][a-zA-Z0-9_-]{0,99})\]\]";

	static readonly Regex Rx = new(TokenPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// Distinct mention tokens found across the given raw markdown bodies (nulls/empties skipped).
	public static IReadOnlyCollection<string> ExtractTokens(IEnumerable<string?> bodies)
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
