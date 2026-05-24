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
	readonly AdminOptions _admin;

	public LoginModel(YobaBoxDb db, IOptions<AdminOptions> options)
	{
		_db = db;
		_admin = options.Value;
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

		var authenticated = false;

		if (string.Equals(_admin.Username, username, StringComparison.Ordinal)
			&& AdminPasswordHasher.Verify(password, _admin.PasswordHash))
		{
			authenticated = true;
		}
		else
		{
			var user = _db.Users.FirstOrDefault(u => u.Username == username);
			if (user is not null && AdminPasswordHasher.Verify(password, user.PasswordHash))
				authenticated = true;
		}

		if (!authenticated)
		{
			ErrorMessage = "Invalid username or password.";
			return Page();
		}

		var identity = new ClaimsIdentity(
			[new Claim(ClaimTypes.Name, username)],
			CookieAuthenticationDefaults.AuthenticationScheme);
		await HttpContext.SignInAsync(
			CookieAuthenticationDefaults.AuthenticationScheme,
			new ClaimsPrincipal(identity));

		return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
			? LocalRedirect(returnUrl)
			: RedirectToPage("/Index");
	}
}
