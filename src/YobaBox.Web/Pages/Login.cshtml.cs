using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly AdminOptions _adminOptions;

	public LoginModel(YobaBoxDb db, IOptions<AdminOptions> adminOptions)
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

		var memberships = _db.WorkspaceMembers
			.Where(m => m.UserId == user.Id)
			.ToList();

		var activeWs = memberships.FirstOrDefault()?.WorkspaceKey ?? "$system";
		var rolesClaim = WorkspaceRoleAuthorizationHandler.SerializeRoles(
			memberships.Select(m => (m.WorkspaceKey, m.Role)));

		var claims = new List<Claim>
		{
			new(ClaimTypes.Name, user.Username),
			new(YobaBoxClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
			new(YobaBoxClaims.ActiveWorkspace, activeWs),
			new(YobaBoxClaims.WorkspaceRoles, rolesClaim),
		};

		// Bootstrap admin (username matches Admin:Username from appsettings) gets the sysadmin claim.
		// See doc/settings-taxonomy.md §4 for the permission model.
		if (!string.IsNullOrEmpty(_adminOptions.Username)
			&& string.Equals(user.Username, _adminOptions.Username, StringComparison.Ordinal))
		{
			claims.Add(new Claim(YobaBoxClaims.IsSysAdmin, "true"));
		}

		var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity));

		HttpContext.Response.Cookies.Append(
			YobaBox.Web.Navigation.WorkspaceSwitchEndpoint.CookieName,
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
}
