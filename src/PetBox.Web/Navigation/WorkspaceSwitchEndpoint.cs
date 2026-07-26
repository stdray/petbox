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

	// THE TENANT IS THE FORM FIELD `ws`, AND IT IS A WORKSPACE — declared, and therefore enforced for
	// EVERY kind of principal. The membership check this replaced was
	//
	//     if (!isSysAdmin && long.TryParse(userIdRaw, …, out var userId)) { …roles… }
	//
	// which reads as "members are checked, sysadmin is waved through" and is actually "…and so is anyone
	// with no user-id claim at all". An api-key principal has none, so for api keys the check did not run
	// — the cross-tenant probe measured this endpoint SERVING the attacker's key a 302 into the victim's
	// workspace once it started sending form bodies. ITenantAuthorizer has no such gap: a cookie is
	// answered on membership (sysadmin free pass included, inside HasWorkspaceRoleAtLeast), an api key on
	// whether its project belongs to that workspace, and a principal that is neither is refused.
	//
	// Any role is enough to SWITCH (Viewer included) — the question is membership, not admin — which is
	// exactly the threshold ITenantAuthorizer.UserAsync uses.
	[TenantFrom(TenantSource.BodyField, "ws", TenantKind.Workspace)]
	static IResult Switch(
		HttpContext ctx,
		[FromForm] string? ws,
		[FromForm] string? zone)
	{
		// `ws` is nullable on the binding so an empty-form POST surfaces as
		// a clean 400, not an unhandled BadHttpRequestException with stack
		// trace into the error log. The sidebar's onchange-submit form is
		// the main offender — Alpine fires it before a value is selected.
		// (Unreachable for a BLANK value now: an unresolved tenant is refused above the handler. It stays
		// for the empty-form case the comment names, and because a declaration is not a shape validator.)
		if (string.IsNullOrWhiteSpace(ws))
			return Results.BadRequest(new ErrorResponse("ws is required"));

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
