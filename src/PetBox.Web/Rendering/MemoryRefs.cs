using System.Text.RegularExpressions;

namespace PetBox.Web.Rendering;

// Mention-scanning helper for MEMORY-ENTRY KEYS in a markdown body (spec: memory-key-mention-link),
// the sibling of NodeRefs. The renderer applies the precise exclusions (code spans/blocks, existing
// links); this cheap pre-scan over raw markdown gathers candidate keys so the page can batch-resolve
// them in ONE query. Over-matching inside a code span is harmless — an extra resolved-but-unused map
// entry never becomes a link (the renderer won't touch a code span).
public static class MemoryRefs
{
	// The two GENERATED memory-key shapes: `m-<32hex>` (memory_remember) and `ac-<12hex>` (the
	// autocapture job). A hand-chosen key ("index", "canon-notes") is deliberately NOT linkified —
	// it is an ordinary English-ish word and would turn prose into links.
	// Edges must not touch a word char OR a hyphen — the same custom lookarounds MarkdownRenderer's
	// commit-hash rule uses, so `xx-m-…` / `m-…-tail` are not keys. The two rules cannot collide:
	// the hex tail of a key is preceded by `-`, which the hash rule's lookbehind rejects.
	public const string KeyPattern = @"(?<![\w-])(m-[0-9a-fA-F]{32}|ac-[0-9a-fA-F]{12})(?![\w-])";

	static readonly Regex Rx = new(KeyPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// Distinct candidate keys across the given raw markdown bodies (nulls/empties skipped).
	public static IReadOnlyCollection<string> ExtractKeys(IEnumerable<string?> bodies)
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
