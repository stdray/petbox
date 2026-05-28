using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;

namespace YobaBox.Web.Navigation;

public static class WorkspaceSwitchEndpoint
{
	public const string CookieName = "yb_ws";

	public static void MapWorkspaceSwitch(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/ui/workspace", Switch)
			.RequireAuthorization()
			.DisableAntiforgery();
	}

	static async Task<IResult> Switch(
		HttpContext ctx,
		YobaBoxDb db,
		[FromForm] string? ws,
		[FromForm] string? returnUrl)
	{
		// `ws` is nullable on the binding so an empty-form POST surfaces as
		// a clean 400, not an unhandled BadHttpRequestException with stack
		// trace into the error log. The sidebar's onchange-submit form is
		// the main offender — Alpine fires it before a value is selected.
		if (string.IsNullOrWhiteSpace(ws))
			return Results.BadRequest(new { error = "ws is required" });

		var isSysAdmin = ctx.User.HasClaim(YobaBoxClaims.IsSysAdmin, "true");
		var userIdRaw = ctx.User.FindFirst(YobaBoxClaims.UserId)?.Value;
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

		var dest = !string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
			? returnUrl
			: Routes.Workspace(ws);
		return Results.LocalRedirect(dest);
	}
}
