using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Health;

// Push side of the health subsystem: services POST their status structure here.
// Pull side lives in PetBox.Dashboard.HealthPoller. Reports are append-only,
// identified by (Svc, canonical Tags); the status page shows the latest per key.
public static class HealthApi
{
	public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/health", PushAsync)
			.Produces<OkResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ApiKey");
	}

	public sealed record HealthPushRequest(
		string Svc,
		string? Name,
		Dictionary<string, string>? Tags,
		string? Version,
		string? Sha,
		string? BuildDate,
		string Status);

	static async Task<IResult> PushAsync(HttpContext ctx, PetBoxDb db, HealthPushRequest req, CancellationToken ct)
	{
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Contains(ApiKeyScopes.HealthWrite, StringComparer.Ordinal))
			return Results.Forbid();

		if (req is null || string.IsNullOrWhiteSpace(req.Svc) || string.IsNullOrWhiteSpace(req.Status))
			return TypedResults.BadRequest(new ErrorResponse("svc and status are required"));

		var tags = req.Tags ?? [];
		if (!tags.TryGetValue("project", out var project) || string.IsNullOrWhiteSpace(project))
			return TypedResults.BadRequest(new ErrorResponse("tags.project is required"));

		// A project-scoped key may only report for its own project.
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, project))
			return Results.Forbid();

		await db.InsertAsync(new HealthReport
		{
			Svc = req.Svc.Trim(),
			Name = req.Name,
			Tags = HealthTags.Canonical(tags),
			Version = req.Version,
			Sha = req.Sha,
			BuildDate = req.BuildDate,
			Status = req.Status.Trim(),
			ReceivedAt = DateTime.UtcNow,
			Source = "push",
		}, token: ct);

		return TypedResults.Ok(new OkResponse(true));
	}
}
