using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;

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

	static async Task<IResult> Switch(
		HttpContext ctx,
		PetBoxDb db,
		[FromForm] string? ws)
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
			var member = await db.WorkspaceMembers
				.FirstOrDefaultAsync((Core.Models.WorkspaceMember m) => m.UserId == userId && m.WorkspaceKey == ws);
			if (member is null)
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
		return Results.LocalRedirect(Routes.Workspace(ws));
	}
}
