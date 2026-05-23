using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace YobaBox.Core.Auth;

public static class AuthApi
{
	public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/auth/validate", Validate).RequireAuthorization();
	}

	static IResult Validate(HttpContext context)
	{
		var user = context.User;
		if (user.Identity is not { IsAuthenticated: true })
			return Results.Json(new { valid = false }, statusCode: 401);

		var projectKey = user.FindFirstValue("project");
		var scopes = user.FindFirstValue("scopes");

		if (string.IsNullOrEmpty(projectKey))
			return Results.Json(new { valid = false }, statusCode: 401);

		return Results.Json(new { project = projectKey, scopes = scopes ?? string.Empty });
	}
}
