using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Doc;

// Public agent CHEATSHEET for the spec/work/idea methodology — the operational
// minimum an agent needs to drive the boards correctly. The "why" lives on the
// Philosophy page; this page is the contract. Anonymous: fetch by URL, no key. The prose
// is the markdown canon Pages/Doc/content/methodology.md, rendered through the shared renderer.
[AllowAnonymous]
[TenantExempt(TenantExemption.Public,
	"the public documentation tree: no tenant in the route, no credential required, and nothing a "
	+ "tenant owns is read — the prose is Pages/Doc/content/*.md, shipped with the build")]
public sealed class MethodologyModel : PageModel
{
	readonly DocContent _docs;

	public MethodologyModel(DocContent docs) => _docs = docs;

	public string Markdown { get; private set; } = "";

	public void OnGet() => Markdown = _docs.Read("methodology");
}
