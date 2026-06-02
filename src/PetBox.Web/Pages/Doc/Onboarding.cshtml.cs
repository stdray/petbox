using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Doc;

// Public, staged onboarding runbook for connecting a coding agent end-to-end:
// key → env var → MCP registration → comprehension → skill → first real plan.
// Each stage has an explicit validation gate so a human (or the agent) can confirm
// it actually worked before moving on. Anonymous — the agent fetches it by URL.
[AllowAnonymous]
public sealed class OnboardingModel : PageModel
{
}
