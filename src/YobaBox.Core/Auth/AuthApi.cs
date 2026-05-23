using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace YobaBox.Core.Auth;

public static class AuthApi
{
	public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/auth/validate", Validate);
	}

	static IResult Validate(HttpContext context)
	{
		var projectKey = context.Items["ProjectKey"] as string;
		var scopes = context.Items["Scopes"] as string;

		if (string.IsNullOrEmpty(projectKey))
			return Results.Json(new { valid = false }, statusCode: 401);

		return Results.Json(new { project = projectKey, scopes = scopes ?? string.Empty });
	}
}
