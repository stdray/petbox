using System.Text.RegularExpressions;

namespace PetBox.Tasks.Data;

// A plan node's address is a FLAT, board-unique slug (spec-flat-tags: the old l1/l2/l3
// path hierarchy is gone — vertical structure is now the `part_of` edge, and grouping is
// a tag projection). The temporal engine is string-keyed and payload-agnostic; this type
// just owns slug validation/normalization (lowercased, one segment). NodeId remains the
// stable identity that edges/tags bind to.
public static partial class TaskSlug
{
	// Same spec as boards/logs: starts a-z, then a-z/0-9/_/- up to 100 chars. A single
	// segment — '/' is no longer a path separator and is not allowed.
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SlugRegex();

	// Normalize + validate a slug. Trims, lowercases, then enforces the spec. Throws on
	// empty or an invalid segment (including any '/').
	public static string Validate(string? slug)
	{
		var s = slug?.Trim().ToLowerInvariant();
		if (string.IsNullOrEmpty(s))
			throw new ArgumentException("a node key (slug) is required", nameof(slug));
		if (!SlugRegex().IsMatch(s))
			throw new ArgumentException($"'{slug}' is not a valid node key; must match ^[a-z][a-z0-9_-]{{0,99}}$ (a single flat segment — no '/')", nameof(slug));
		return s;
	}

	public static bool IsValid(string? slug) =>
		!string.IsNullOrWhiteSpace(slug) && SlugRegex().IsMatch(slug.Trim().ToLowerInvariant());
}
