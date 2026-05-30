using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages.Doc;

// Public documentation catalog. Anonymous on purpose: agents (and humans) can
// reach it by URL with no cookie or API key. Lists the available guides.
[AllowAnonymous]
public sealed class IndexModel : PageModel
{
}
