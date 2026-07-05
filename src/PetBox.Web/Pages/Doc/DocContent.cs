namespace PetBox.Web.Pages.Doc;

// Loads the public /doc page bodies from their markdown canon, which ships ALONGSIDE the app
// (Pages/Doc/content/*.md, copied to the publish output — see PetBox.Web.csproj <Content Include>).
// The md files are the SINGLE SOURCE for the doc prose: the pages render them through the shared
// IMarkdownRenderer (via _MdBody), so there is no hand-synced HTML duplicate to drift. Resolved
// relative to IWebHostEnvironment.ContentRootPath, which is the app root both in the container
// (/app, where the content is copied) and under WebApplicationFactory (the project directory) —
// so a page never 500s for a missing file in prod.
public sealed class DocContent
{
	readonly string _dir;

	public DocContent(IWebHostEnvironment env) =>
		_dir = Path.Combine(env.ContentRootPath, "Pages", "Doc", "content");

	// Read the raw markdown for a page slug and apply the dynamic substitutions — the only
	// non-static bits of a doc page: `origin` (this instance's base URL) and `mcp` (its MCP
	// endpoint), both derived per-request so they stay correct behind a reverse proxy. A page
	// references them as `{{origin}}` / `{{mcp}}` placeholders in its md; a page with no dynamic
	// bit passes no substitutions. Slugs are fixed literals from the page models, never user input.
	public string Read(string slug, IReadOnlyDictionary<string, string>? substitutions = null)
	{
		var text = File.ReadAllText(Path.Combine(_dir, slug + ".md"));
		if (substitutions is not null)
			foreach (var (key, value) in substitutions)
				text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
		return text;
	}
}
