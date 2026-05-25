using System.Globalization;
using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;

namespace YobaBox.Web.Navigation;

public static class WorkspaceSwitchEndpoint
{
	public const string CookieName = "yb_ws";

	public static void MapWorkspaceSwitch(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/ui/workspace", Switch).RequireAuthorization();
	}

	static async Task<IResult> Switch(HttpContext ctx, YobaBoxDb db, string ws, string? returnUrl)
	{
		if (string.IsNullOrWhiteSpace(ws))
			return Results.BadRequest(new { error = "ws is required" });

		var userIdRaw = ctx.User.FindFirst(YobaBoxClaims.UserId)?.Value;
		if (long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
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
			: "/ui/dashboard";
		return Results.LocalRedirect(dest);
	}
}
