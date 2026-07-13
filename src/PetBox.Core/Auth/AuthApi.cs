using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Core.Auth;

public static class AuthApi
{
	// 200: the key's identity. 401: {"valid": false}.
	// `workspace` is additive and LAST: the workspace the key's project lives in, so a client
	// (the CLI) can stop guessing a personal workspace. Null when it cannot be resolved — a
	// valid key must still validate.
	public sealed record AuthValidResponse(string Project, string Scopes, string? Workspace);
	public sealed record AuthInvalidResponse(bool Valid);

	public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/auth/validate", Validate)
			.Produces<AuthValidResponse>()
			.Produces<AuthInvalidResponse>(StatusCodes.Status401Unauthorized)
			.RequireAuthorization("ApiKey");
	}

	// IProjectCatalog, not ICoreDbFactory: the one thing this endpoint reads from core.db is "which
	// workspace owns this project", and the catalog already owns that question
	// (WorkspaceKeyOfAsync). The endpoint asks; it does not open the database.
	static async Task<IResult> Validate(HttpContext context, IProjectCatalog projects, CancellationToken ct)
	{
		var user = context.User;
		if (user.Identity is not { IsAuthenticated: true })
			return Results.Json(new AuthInvalidResponse(false), statusCode: 401);

		var projectKey = user.FindFirstValue("project");
		var scopes = user.FindFirstValue("scopes");

		if (string.IsNullOrEmpty(projectKey))
			return Results.Json(new AuthInvalidResponse(false), statusCode: 401);

		return TypedResults.Ok(new AuthValidResponse(
			projectKey, scopes ?? string.Empty, await ResolveWorkspaceAsync(user, projects, projectKey, ct)));
	}

	// Prefer an explicit workspace claim when the identity carries one; otherwise the project row
	// is the authority (an API key is project-scoped, a project belongs to exactly one workspace).
	// Never throws: an unresolvable workspace is reported as null, not as a failed validation.
	static async Task<string?> ResolveWorkspaceAsync(
		ClaimsPrincipal user, IProjectCatalog projects, string projectKey, CancellationToken ct)
	{
		var claimed = user.FindFirstValue(PetBoxClaims.ActiveWorkspace);
		if (!string.IsNullOrWhiteSpace(claimed)) return claimed;

		try
		{
			var ws = await projects.WorkspaceKeyOfAsync(projectKey, ct);
			return string.IsNullOrWhiteSpace(ws) ? null : ws;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}
}
