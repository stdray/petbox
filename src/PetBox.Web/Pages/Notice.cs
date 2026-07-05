using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PetBox.Web.Pages;

// Shared success-feedback mechanism for mutating POSTs. A handler that mutates then
// redirects (Post/Redirect/Get) records a one-line success message here; it rides across
// the redirect in TempData and the layouts render it exactly once through _Notice.cshtml,
// which consumes it. This keeps post-mutation feedback consistent instead of the ad-hoc,
// per-page success alerts (and query-string flags) that grew up across the app.
static class Notice
{
	// TempData key for the carried success message. Read (and thereby consumed) by _Notice.cshtml.
	public const string SuccessKey = "Notice.Success";

	// TempData key for a one-time secret (a freshly minted API key) shown once after a
	// redirect. Distinct from SuccessKey so the page can render it in its own copy-me chrome.
	public const string NewKeyKey = "Notice.NewKey";

	// Record a success message to show after the next redirect. No-op when TempData is
	// unavailable (a bare PageModel under a unit test with no PageContext), so mutating
	// handlers stay unit-testable without wiring a full request pipeline.
	public static void NotifySuccess(this PageModel page, string message)
	{
		var tempData = page.TempData;
		if (tempData is null)
			return;
		tempData[SuccessKey] = message;
	}

	// Carry a one-time secret across a redirect (Post/Redirect/Get for the mint flows), so a
	// refresh of the landing page re-POSTs nothing and the secret vanishes after its single render.
	public static void CarryNewKey(this PageModel page, string keyValue)
	{
		var tempData = page.TempData;
		if (tempData is null)
			return;
		tempData[NewKeyKey] = keyValue;
	}

	// Read + consume the carried one-time secret on the GET after a mint redirect.
	public static string? TakeNewKey(this PageModel page)
	{
		var tempData = page.TempData;
		return tempData?[NewKeyKey] as string;
	}
}
