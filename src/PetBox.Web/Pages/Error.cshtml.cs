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

	public void OnGet() => RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

	// UseExceptionHandler re-executes /Error with the ORIGINAL request method — without
	// these handlers a failed POST/PUT/DELETE turns into a secondary 500 on the error
	// page itself, masking the real failure.
	public void OnPost() => OnGet();

	public void OnPut() => OnGet();

	public void OnDelete() => OnGet();

	public void OnPatch() => OnGet();
}
