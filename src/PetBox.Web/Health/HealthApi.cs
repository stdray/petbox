using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Health;

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

	// Authorize, validate, delegate. The handler opens no database — it hands a validated report to
	// IHealthReportService, which owns the table (AGENTS.md: the database is visible only in the
	// service layer). Note the ordering that the old inline version got backwards: the connection
	// used to be opened at the top, BEFORE the scope check, so every forbidden push paid for one.
	static async Task<IResult> PushAsync(HttpContext ctx, IHealthReportService health, IProjectCatalog catalog, HealthPushRequest req, CancellationToken ct)
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
		if (!await ProjectScope.AuthorizesAsync(ctx.User, project, catalog, ct))
			return Results.Forbid();

		await health.RecordPushAsync(
			new HealthReportInput(req.Svc, req.Name, tags, req.Version, req.Sha, req.BuildDate, req.Status), ct);

		return TypedResults.Ok(new OkResponse(true));
	}
}
