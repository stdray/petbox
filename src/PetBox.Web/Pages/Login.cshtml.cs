using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
	// ICredentialAuthenticator, NOT IUserAdminService: this page is reachable by anyone at all, and
	// the admin service can reset any account's password. The door this page holds can do exactly one
	// thing — check a password it was handed — and it hands back no hash. See CredentialAuthenticator.
	readonly ICredentialAuthenticator _credentials;

	public LoginModel(ICredentialAuthenticator credentials) => _credentials = credentials;

	[BindProperty(SupportsGet = true)]
	public string? ReturnUrl { get; set; }

	public string? Username { get; set; }
	public string? ErrorMessage { get; set; }

	// Already signed in → there is nothing to do here. Showing the form to an authenticated user
	// is what made a 403 look like a session expiry (the old AccessDeniedPath pointed here) and
	// what made re-logging-in feel like an endless loop.
	public IActionResult OnGet() =>
		User.Identity?.IsAuthenticated == true
			? Redirect("/")
			: Page();

	public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl)
	{
		Username = username;
		ReturnUrl = returnUrl;

		// Empty fields, an unknown name, a wrong password, and the bootstrap-admin lockdown are all
		// the same kind of answer — the service decides, this page renders. (The lockdown rule moved
		// with it: once another $system admin exists, PETBOX_ADMIN_* can no longer sign in.)
		var result = await _credentials.AuthenticateAsync(username, password, HttpContext.RequestAborted);
		if (result is not CredentialResult.Authenticated(var user))
		{
			ErrorMessage = ((CredentialResult.Rejected)result).Reason;
			return Page();
		}

		// No membership → NO active workspace. The old `?? "$system"` fallback handed every fresh
		// account a claim (and a yb_ws cookie) for a workspace it had no membership in — which is
		// how a brand-new user landed on /ui/$system/$system. Null here; the landing page renders
		// the "no workspaces" empty state instead (auth-denied-and-empty-state).
		var activeWs = user.Memberships.Count > 0 ? user.Memberships[0].WorkspaceKey : null;
		var rolesClaim = WorkspaceRoleAuthorizationHandler.SerializeRoles(
			user.Memberships.Select(m => (m.WorkspaceKey, m.Role)));

		var claims = new List<Claim>
		{
			new(ClaimTypes.Name, user.Username),
			new(PetBoxClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
			new(PetBoxClaims.WorkspaceRoles, rolesClaim),
		};
		if (activeWs is not null)
			claims.Add(new Claim(PetBoxClaims.ActiveWorkspace, activeWs));

		// Bootstrap admin (username matches Admin:Username from appsettings) gets the sysadmin claim.
		// See doc/settings-taxonomy.md §4 for the permission model.
		if (user.IsBootstrapAdmin)
			claims.Add(new Claim(PetBoxClaims.IsSysAdmin, "true"));

		var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
		// IsPersistent makes the browser write Expires/Max-Age (driven by ExpireTimeSpan),
		// so the cookie survives browser close instead of being a session cookie.
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity),
			new AuthenticationProperties { IsPersistent = true });

		if (activeWs is not null)
		{
			HttpContext.Response.Cookies.Append(
				PetBox.Web.Navigation.WorkspaceSwitchEndpoint.CookieName,
				activeWs,
				new CookieOptions
				{
					HttpOnly = false,
					SameSite = SameSiteMode.Lax,
					Expires = DateTimeOffset.UtcNow.AddDays(365),
					IsEssential = true,
					Path = "/",
				});
		}

		return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
			? LocalRedirect(returnUrl)
			: RedirectToPage("/Index");
	}
}
