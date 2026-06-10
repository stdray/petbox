using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PetBox.Core.Auth;

public static class AuthApi
{
	// 200: the key's identity. 401: {"valid": false}.
	public sealed record AuthValidResponse(string Project, string Scopes);
	public sealed record AuthInvalidResponse(bool Valid);

	public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/auth/validate", Validate)
			.Produces<AuthValidResponse>()
			.Produces<AuthInvalidResponse>(StatusCodes.Status401Unauthorized)
			.RequireAuthorization("ApiKey");
	}

	static IResult Validate(HttpContext context)
	{
		var user = context.User;
		if (user.Identity is not { IsAuthenticated: true })
			return Results.Json(new AuthInvalidResponse(false), statusCode: 401);

		var projectKey = user.FindFirstValue("project");
		var scopes = user.FindFirstValue("scopes");

		if (string.IsNullOrEmpty(projectKey))
			return Results.Json(new AuthInvalidResponse(false), statusCode: 401);

		return TypedResults.Ok(new AuthValidResponse(projectKey, scopes ?? string.Empty));
	}
}
