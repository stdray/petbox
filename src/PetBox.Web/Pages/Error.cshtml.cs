using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
	public string? RequestId { get; set; }

	public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

	// The status code the request failed with. Supplied by UseStatusCodePagesWithReExecute
	// as `?code={0}` when it re-executes this page for a bare 4xx/5xx (e.g. a 404 for an
	// unknown path or an unknown/non-member workspace key). Null for a direct hit / the
	// UseExceptionHandler("/Error") 500 path.
	public int? ErrorCode { get; private set; }

	// A missing resource (unknown route, unknown/non-member workspace key) gets the
	// friendly "not found" copy; everything else keeps the generic error copy.
	public bool IsNotFound => ErrorCode == 404;

	public void OnGet()
	{
		RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
		if (int.TryParse(Request.Query["code"], out var code) && code is >= 400 and < 600)
			ErrorCode = code;
	}

	// UseExceptionHandler re-executes /Error with the ORIGINAL request method — without
	// these handlers a failed POST/PUT/DELETE turns into a secondary 500 on the error
	// page itself, masking the real failure.
	public void OnPost() => OnGet();

	public void OnPut() => OnGet();

	public void OnDelete() => OnGet();

	public void OnPatch() => OnGet();
}
