using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Data;

namespace PetBox.Core.Auth;

public static class AuthApi
{
	// 200: the key's identity. 401: {"valid": false}.
	// `workspace` is additive and LAST: the workspace the key's project lives in, so a client
	// (the CLI) can stop guessing a personal workspace. Null when it cannot be resolved — a
	// valid key must still validate.
	private sealed record AuthValidResponse(string Project, string Scopes, string? Workspace);
	private sealed record AuthInvalidResponse(bool Valid);

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
	//
	// `identity`: the whole response is facts about the CALLER'S OWN key — its project, its scopes, the
	// workspace that project lives in — read off the caller's own claims. There is no second tenant to
	// name, so there is nothing for the tenant axis to decide. Not `public`: the ApiKey policy still has
	// to authenticate the key, and an exemption in this class suspends only the tenant check.
	//
	// Deliberately NOT [TenantFrom(CallerDefault)], which would look tempting because the answer is
	// derived from the key's project: that source means the caller is ACTING on that tenant, and the
	// difference shows on a cross-project key. Its claim is "*" — a valid identity to report — while
	// CallerDefault would demand a resolvable single project and refuse the key that has no
	// `project_default`, turning "here is who you are" into a 403 for the keys most likely to be asking.
	[TenantExempt(TenantExemption.Identity,
		"reports the caller's own project, scopes and workspace off its own claims; the caller IS the "
		+ "subject and there is no other tenant to name")]
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
