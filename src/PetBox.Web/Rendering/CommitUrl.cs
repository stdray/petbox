namespace PetBox.Web.Rendering;

// Host-agnostic commit-view URL builder. A project may declare a URL template carrying a literal
// {sha} placeholder (RepoSettings.CommitUrlTemplate); this expands it for a given commit ref/hash.
// A template is only usable when it is non-empty AND actually carries the placeholder — otherwise
// there is nothing to expand and callers fall back to plain text (the pre-feature behavior).
public static class CommitUrl
{
	public const string Placeholder = "{sha}";

	// True when `template` can be expanded (non-empty and contains {sha}). The markdown renderer
	// and the commit-ref chip both gate on this so an unset/garbled template is a no-op.
	public static bool HasTemplate(string? template) =>
		!string.IsNullOrEmpty(template) && template.Contains(Placeholder, StringComparison.Ordinal);

	// Expanded URL for `sha`, or null when the template is unusable (caller renders plain text).
	public static string? For(string? template, string sha) =>
		HasTemplate(template) ? template!.Replace(Placeholder, sha, StringComparison.Ordinal) : null;
}
