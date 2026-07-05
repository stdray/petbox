using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Doc;

// Public overview of PetBox: what it is, the modules, and how to consume them
// (REST / MCP + the published client libraries). Anonymous — an agent can read it
// right after connecting to understand the platform it's building on. The prose is the
// markdown canon Pages/Doc/content/overview.md, rendered through the shared renderer;
// `{{origin}}` in the client-library snippets is substituted with this instance's base URL.
[AllowAnonymous]
public sealed class OverviewModel : PageModel
{
	readonly DocContent _docs;

	public OverviewModel(DocContent docs) => _docs = docs;

	public string Markdown { get; private set; } = "";

	public void OnGet()
	{
		var origin = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
		Markdown = _docs.Read("overview", new Dictionary<string, string> { ["origin"] = origin });
	}
}
