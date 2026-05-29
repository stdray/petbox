using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
	readonly PetBoxDb _db;
	readonly AdminOptions _adminOptions;

	public LoginModel(PetBoxDb db, IOptions<AdminOptions> adminOptions)
	{
		_db = db;
		_adminOptions = adminOptions.Value;
	}

	[BindProperty(SupportsGet = true)]
	public string? ReturnUrl { get; set; }

	public string? Username { get; set; }
	public string? ErrorMessage { get; set; }

	public void OnGet() { }

	public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl)
	{
		Username = username;
		ReturnUrl = returnUrl;

		if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
		{
			ErrorMessage = "Enter a username and password.";
			return Page();
		}

		var user = _db.Users.FirstOrDefault(u => u.Username == username);
		if (user is null || !AdminPasswordHasher.Verify(password, user.PasswordHash))
		{
			ErrorMessage = "Invalid username or password.";
			return Page();
		}

		// Bootstrap-admin lockdown: once another $system administrator exists, the env-admin
		// (PETBOX_ADMIN_*) account can no longer sign in. Set PETBOX_ADMIN_FORCE=true to re-enable
		// it for recovery. See AGENTS.md. No lockout risk: if env-admin is the only admin, nothing changes.
		var isBootstrapAdmin = !string.IsNullOrEmpty(_adminOptions.Username)
			&& string.Equals(user.Username, _adminOptions.Username, StringComparison.Ordinal);
		if (isBootstrapAdmin && !AdminForceEnabled())
		{
			var otherSysAdminExists = _db.WorkspaceMembers
				.Any(m => m.WorkspaceKey == "$system" && m.Role == WorkspaceRole.Admin && m.UserId != user.Id);
			if (otherSysAdminExists)
			{
				ErrorMessage = "The bootstrap admin account is disabled because another administrator exists. Sign in with your own account.";
				return Page();
			}
		}

		var memberships = _db.WorkspaceMembers
			.Where(m => m.UserId == user.Id)
			.ToList();

		var activeWs = memberships.FirstOrDefault()?.WorkspaceKey ?? "$system";
		var rolesClaim = WorkspaceRoleAuthorizationHandler.SerializeRoles(
			memberships.Select(m => (m.WorkspaceKey, m.Role)));

		var claims = new List<Claim>
		{
			new(ClaimTypes.Name, user.Username),
			new(PetBoxClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
			new(PetBoxClaims.ActiveWorkspace, activeWs),
			new(PetBoxClaims.WorkspaceRoles, rolesClaim),
		};

		// Bootstrap admin (username matches Admin:Username from appsettings) gets the sysadmin claim.
		// See doc/settings-taxonomy.md §4 for the permission model.
		if (!string.IsNullOrEmpty(_adminOptions.Username)
			&& string.Equals(user.Username, _adminOptions.Username, StringComparison.Ordinal))
		{
			claims.Add(new Claim(PetBoxClaims.IsSysAdmin, "true"));
		}

		var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
		// IsPersistent makes the browser write Expires/Max-Age (driven by ExpireTimeSpan),
		// so the cookie survives browser close instead of being a session cookie.
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity),
			new AuthenticationProperties { IsPersistent = true });

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

		return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
			? LocalRedirect(returnUrl)
			: RedirectToPage("/Index");
	}

	static bool AdminForceEnabled() =>
		string.Equals(Environment.GetEnvironmentVariable("PETBOX_ADMIN_FORCE"), "true", StringComparison.OrdinalIgnoreCase);
}
