using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Doc;

// Public, staged onboarding runbook for connecting a coding agent end-to-end:
// key → env var → MCP registration → comprehension → skill → first real plan.
// Each stage has an explicit validation gate so a human (or the agent) can confirm
// it actually worked before moving on. Anonymous — the agent fetches it by URL. The prose
// is the markdown canon Pages/Doc/content/onboarding.md, rendered through the shared renderer.
[AllowAnonymous]
[TenantExempt(TenantExemption.Public,
	"the public documentation tree: no tenant in the route, no credential required, and nothing a "
	+ "tenant owns is read — the prose is Pages/Doc/content/*.md, shipped with the build")]
public sealed class OnboardingModel : PageModel
{
	readonly DocContent _docs;

	public OnboardingModel(DocContent docs) => _docs = docs;

	public string Markdown { get; private set; } = "";

	public void OnGet() => Markdown = _docs.Read("onboarding");
}
