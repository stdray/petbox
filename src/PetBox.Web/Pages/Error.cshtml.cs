using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages;

[AllowAnonymous]
// The generic status-code/exception page every failing request in the browser plane is re-executed
// into (Program.cs UseStatusCodePagesWithReExecute). It shows a request id and nothing else — there
// is no tenant to name, and it MUST stay reachable for a request that was just refused for a tenant
// it may not touch, or a refusal turns into a second refusal and the user sees nothing at all.
[TenantExempt(TenantExemption.Public,
	"the error page: anonymous, carries only a request id, and is the destination a refused request "
	+ "is re-executed into — a tenant check on it would refuse the explanation as well")]
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
