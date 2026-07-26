using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Web.Navigation;

namespace PetBox.Web.Pages;

// The cookie handler's AccessDeniedPath. A forbidden (403) request used to be redirected to
// /Login — an [AllowAnonymous] page that happily re-renders the sign-in form to a user who is
// already signed in, so "you may not see this" was indistinguishable from "you were signed out"
// and signing in again just bounced back to the form (auth-denied-and-empty-state).
//
// Responds with a real 403 (status-code re-execution is switched off for this request so the
// generic /Error page does not swallow the explanation).
[Authorize]
// It reports the CALLER's own situation — their username, whether they hold any workspace at all,
// and where their own home is — plus the URL they typed, which they already know. No tenant is named
// by the route and none is read.
//
// It is also the endpoint plane's own refusal DESTINATION (Program.cs cookie AccessDeniedPath, and
// TenantEnforcementMiddleware's ForbidAsync lands here too). A tenant check on this page would
// refuse the refusal and bounce the browser between two denials, so the exemption is what keeps the
// boundary explainable rather than a courtesy.
[TenantExempt(TenantExemption.Identity,
	"answers with the caller's own account and their own home workspace; the ReturnUrl is the "
	+ "caller's own input, and this page is where every other refusal on this plane is sent")]
public sealed class AccessDeniedModel(INavigationContext nav) : PageModel
{
	// What the user was trying to reach — the cookie handler appends it as ?ReturnUrl=.
	[BindProperty(SupportsGet = true)]
	public string? ReturnUrl { get; set; }

	public string? Username { get; private set; }
	public bool HasWorkspace { get; private set; }
	public string? HomeUrl { get; private set; }

	public IActionResult OnGet()
	{
		Username = User.Identity?.Name;
		HasWorkspace = nav.HasWorkspace;
		HomeUrl = nav.CurrentWorkspaceKey is { } ws ? Routes.Workspace(ws) : null;

		if (HttpContext.Features.Get<IStatusCodePagesFeature>() is { } statusCodePages)
			statusCodePages.Enabled = false;
		Response.StatusCode = StatusCodes.Status403Forbidden;
		return Page();
	}
}
