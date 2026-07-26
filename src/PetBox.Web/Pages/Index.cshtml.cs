using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages;

[Authorize]
// The app root. It names no tenant: it READS the caller's own current workspace off their own
// claims/cookie and redirects there, or renders the empty state when they hold none. The tenant it
// lands on is therefore always one the caller already has, and NavigationContext.ResolveWorkspace —
// not this page — is what refuses a workspace the session is not a member of.
//
// Mapped by TWO endpoints ("" and "Index"), which is why the declaration cannot be a route source:
// there is no route value on either of them to read.
[TenantExempt(TenantExemption.Identity,
	"resolves the caller's OWN current workspace from their own claims and redirects to it; the route "
	+ "names no tenant and the page reads none it was handed")]
public sealed class IndexModel : PageModel
{
	readonly INavigationContext _nav;
	readonly IAuthorizationService _authz;

	public IndexModel(INavigationContext nav, IAuthorizationService authz)
	{
		_nav = nav;
		_authz = authz;
	}

	public string? Username { get; private set; }

	// Whether to offer the "Create workspace" CTA on the empty state: the SAME CanCreateWorkspace
	// policy the create page itself carries, asked through IAuthorizationService rather than
	// re-derived here — so the button and the gate can never disagree. False → the empty state tells
	// the user to ask an administrator instead of showing a link that would 403.
	public bool CanCreateWorkspace { get; private set; }

	// The app root lands on the current workspace status page — unless the user has no workspace
	// at all, in which case there is nowhere to land: the old code redirected to the "$system"
	// fallback (a workspace a fresh Regular account is not a member of), which now 403s. Render
	// the empty state instead of bouncing the user between a redirect and an access-denied page.
	public async Task<IActionResult> OnGetAsync()
	{
		Username = _nav.Username;
		if (_nav.CurrentWorkspaceKey is { } ws)
			return Redirect(Routes.Workspace(ws));

		// Only reached on the empty state — a user who HAS a workspace was already redirected, so the
		// policy's core-db reads never touch the normal landing path.
		CanCreateWorkspace = (await _authz.AuthorizeAsync(User, "CanCreateWorkspace")).Succeeded;
		return Page();
	}
}
