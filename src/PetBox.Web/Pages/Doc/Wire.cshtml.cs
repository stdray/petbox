using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Doc;

// Public operator guide for the petbox-wire CLI (install/wire, apply, doctor, roles, exit codes,
// offline LKG). Anonymous: an operator or agent can fetch it by URL with no cookie or key. The
// prose is the markdown canon Pages/Doc/content/wire.md, rendered through the shared renderer.
// No substitutions: the page names no host — the CLI carries its own base URL and the registry
// records it per project.
[AllowAnonymous]
public sealed class WireModel : PageModel
{
	readonly DocContent _docs;

	public WireModel(DocContent docs) => _docs = docs;

	public string Markdown { get; private set; } = "";

	public void OnGet() => Markdown = _docs.Read("wire");
}
