namespace PetBox.Core.Settings;

// Per-project repository / VCS integration. Host-agnostic: the project declares a commit-view
// URL template carrying a literal {sha} placeholder, and the UI turns commit refs (node chips)
// and standalone 7–40-hex hashes in rendered bodies into links to that view. Project owns it
// (deepestScope); cascades to Workspace / System like the other project-scoped settings. Empty
// (the default) means "no template declared" → the UI keeps plain text, exactly as before.
public sealed record RepoSettings
{
	[Setting(TopLevel = Scope.Project, Key = "repo.commitUrlTemplate",
		Description = "Commit-view URL template with a literal {sha} placeholder "
			+ "(e.g. https://github.com/user/repo/commit/{sha}). When set, commit refs and "
			+ "7–40-hex hashes in bodies link here. Empty = no links (plain text).")]
	public string CommitUrlTemplate { get; init; } = "";
}
