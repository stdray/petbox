using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Web.Auth;

namespace PetBox.Web.Navigation;

// Sibling of WorkspaceSwitchEndpoint: the sidebar PROJECT selector posts here.
// Same shape (onchange-submit form, no antiforgery, cookie persist + LocalRedirect),
// but scoped to a (workspace, project) pair so the sidebar can render sections for the
// selected project flat, without a multi-project tree.
public static class ProjectSwitchEndpoint
{
	public const string CookieName = "yb_project";

	public static void MapProjectSwitch(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/ui/project", Switch)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization()
			.DisableAntiforgery();
	}

	// An endpoint lambda is pipeline code: it asks services, it does not open core.db (AGENTS.md —
	// the database is visible only in the service layer).
	static async Task<IResult> Switch(
		HttpContext ctx,
		IProjectDirectory projects,
		IWorkspaceMembershipService memberships,
		[FromForm] string? ws,
		[FromForm] string? key,
		[FromForm] string? zone)
	{
		// Both nullable so an empty-form POST (Alpine/onchange firing before a value is
		// selected) surfaces as a clean 400, not an unhandled BadHttpRequestException.
		if (string.IsNullOrWhiteSpace(ws))
			return Results.BadRequest(new ErrorResponse("ws is required"));
		if (string.IsNullOrWhiteSpace(key))
			return Results.BadRequest(new ErrorResponse("key is required"));

		// Validate workspace membership (sysadmin bypasses, mirroring WorkspaceSwitchEndpoint).
		var isSysAdmin = ctx.User.HasClaim(PetBoxClaims.IsSysAdmin, "true");
		var userIdRaw = ctx.User.FindFirst(PetBoxClaims.UserId)?.Value;
		if (!isSysAdmin && long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
		{
			var roles = await memberships.GetRolesAsync(userId);
			if (!roles.Any(m => string.Equals(m.WorkspaceKey, ws, StringComparison.Ordinal)))
				return Results.Forbid();
		}

		// The project must actually live in that workspace — otherwise the cookie would point at a
		// phantom pair and the sidebar's resolver would silently drop it. The workspace is welded into
		// the lookup, so another tenant's project is simply not there.
		var project = await projects.GetInWorkspaceAsync(ws, key);
		if (project is null)
			return Results.BadRequest(new ErrorResponse("unknown project for workspace"));

		ctx.Response.Cookies.Append(CookieName, key, new CookieOptions
		{
			HttpOnly = false,
			SameSite = SameSiteMode.Lax,
			Expires = DateTimeOffset.UtcNow.AddDays(365),
			IsEssential = true,
			Path = "/",
		});

		// Zone-preserving: if the switch came from the admin sidebar, land on the project's
		// admin Info page (its detail/landing page under /ui/admin/...) rather than kicking the
		// user out into the /ui zone. Any other value (or none) keeps the /ui dashboard target.
		var target = string.Equals(zone, "admin", StringComparison.Ordinal)
			? Routes.ProjectSettings(ws, key)
			: Routes.Project(ws, key);
		return Results.LocalRedirect(target);
	}
}
