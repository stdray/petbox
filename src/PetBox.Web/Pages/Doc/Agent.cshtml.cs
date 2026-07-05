using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Doc;

// Public agent-onboarding guide. The durable, key-independent half of the
// "Connect agent" flow lives here so the per-project Connect page (which mints a
// key) can point to it instead of duplicating the tree model / tool list / skill
// template. Anonymous: an agent can fetch it by URL with no cookie or key. The prose is
// the markdown canon Pages/Doc/content/agent.md, rendered through the shared renderer;
// `{{mcp}}` is substituted with this instance's MCP endpoint.
[AllowAnonymous]
public sealed class AgentModel : PageModel
{
	readonly DocContent _docs;

	public AgentModel(DocContent docs) => _docs = docs;

	public string Markdown { get; private set; } = "";

	public void OnGet()
	{
		// This instance's MCP endpoint, derived from the request so it is correct behind a
		// reverse proxy. No key here — the key is shown only on the per-project Connect page.
		var mcp = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/mcp";
		Markdown = _docs.Read("agent", new Dictionary<string, string> { ["mcp"] = mcp });
	}
}
