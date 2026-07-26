using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Doc;

// Public "why" page for the methodology — the model and reasoning behind the rails.
// The operational contract is on the cheatsheet; this is the philosophy. Anonymous. The prose
// is the markdown canon Pages/Doc/content/philosophy.md, rendered through the shared renderer.
[AllowAnonymous]
[TenantExempt(TenantExemption.Public,
	"the public documentation tree: no tenant in the route, no credential required, and nothing a "
	+ "tenant owns is read — the prose is Pages/Doc/content/*.md, shipped with the build")]
public sealed class PhilosophyModel : PageModel
{
	readonly DocContent _docs;

	public PhilosophyModel(DocContent docs) => _docs = docs;

	public string Markdown { get; private set; } = "";

	public void OnGet() => Markdown = _docs.Read("philosophy");
}
