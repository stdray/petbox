using System.Text.RegularExpressions;

namespace PetBox.Tasks.Data;

// A comment's OPTIONAL human-readable address (work `comment-slug-and-refs`). Same flat-slug SHAPE
// as a node key (TaskSlug) on purpose — a `[[#slug]]` mention in a body has to be able to carry it,
// and one shape across the product means an author never has to ask which one this is.
//
// The two differences from TaskSlug are not cosmetic:
//   * it is OPTIONAL — a comment without one is normal, and Validate is never called for it;
//   * its uniqueness scope is the OWNING NODE, not the board. Two comments under two different
//     nodes may carry the same slug; that check lives in CommentService, which has the rows.
public static partial class CommentSlug
{
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex SlugRegex();

	// Normalize + validate. Trims and lowercases (like TaskSlug), then enforces the shape.
	// Throws ArgumentException on an invalid one — CommentService turns that into a per-item
	// refusal in atomic and partial mode alike.
	public static string Validate(string? slug)
	{
		var s = slug?.Trim().ToLowerInvariant();
		if (string.IsNullOrEmpty(s))
			throw new ArgumentException("a comment slug cannot be blank", nameof(slug));
		if (!SlugRegex().IsMatch(s))
			throw new ArgumentException(
				$"'{slug}' is not a valid comment slug; must match ^[a-z][a-z0-9_-]{{0,99}}$ (a single flat segment)",
				nameof(slug));
		return s;
	}
}
