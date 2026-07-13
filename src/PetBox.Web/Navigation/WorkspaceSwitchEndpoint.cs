using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;

namespace PetBox.Web.Navigation;

public static class WorkspaceSwitchEndpoint
{
	public const string CookieName = "yb_ws";

	public static void MapWorkspaceSwitch(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/ui/workspace", Switch)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization()
			.DisableAntiforgery();
	}

	// An endpoint lambda is pipeline code, not a service: it asks IWorkspaceMembershipService whether
	// the caller is a member and never opens core.db itself (AGENTS.md — the database is visible only
	// in the service layer). The membership read is also the seam a cache would live behind, and this
	// endpoint fires on every workspace switch.
	static async Task<IResult> Switch(
		HttpContext ctx,
		IWorkspaceMembershipService members,
		[FromForm] string? ws,
		[FromForm] string? zone)
	{
		// `ws` is nullable on the binding so an empty-form POST surfaces as
		// a clean 400, not an unhandled BadHttpRequestException with stack
		// trace into the error log. The sidebar's onchange-submit form is
		// the main offender — Alpine fires it before a value is selected.
		if (string.IsNullOrWhiteSpace(ws))
			return Results.BadRequest(new ErrorResponse("ws is required"));

		var isSysAdmin = ctx.User.HasClaim(PetBoxClaims.IsSysAdmin, "true");
		var userIdRaw = ctx.User.FindFirst(PetBoxClaims.UserId)?.Value;
		if (!isSysAdmin && long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			// Any role is enough to SWITCH to a workspace (Viewer included) — the check is membership,
			// not admin. RequestAborted, so a switch abandoned mid-flight does not keep reading.
			var roles = await members.GetRolesAsync(userId, ctx.RequestAborted);
			if (!roles.Any(r => string.Equals(r.WorkspaceKey, ws, StringComparison.Ordinal)))
				return Results.Forbid();
		}

		ctx.Response.Cookies.Append(CookieName, ws, new CookieOptions
		{
			HttpOnly = false,
			SameSite = SameSiteMode.Lax,
			Expires = DateTimeOffset.UtcNow.AddDays(365),
			IsEssential = true,
			Path = "/",
		});

		// Always go to the new workspace's landing page. Preserving the
		// previous URL (returnUrl) is unsafe: most petbox pages embed
		// workspaceKey in the path (`/ui/{ws}/...`, `/ui/admin/ws/{ws}/...`),
		// and the cookie-sync middleware would immediately revert the cookie
		// back to the URL's workspace — undoing the switch.
		//
		// Zone-preserving: from the admin sidebar land on the workspace admin
		// Overview; otherwise the /ui workspace landing page.
		var target = string.Equals(zone, "admin", StringComparison.Ordinal)
			? Routes.WorkspaceAdmin(ws)
			: Routes.Workspace(ws);
		return Results.LocalRedirect(target);
	}
}
