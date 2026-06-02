using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Doc;

// Public overview of PetBox: what it is, the modules, and how to consume them
// (REST / MCP + the published client libraries). Anonymous — an agent can read it
// right after connecting to understand the platform it's building on.
[AllowAnonymous]
public sealed class OverviewModel : PageModel
{
	public string Origin => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
}
