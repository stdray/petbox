using JetBrains.Annotations;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
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

	// Constructed by ASP.NET Core JSON model binding on PushAsync below, not by a `new` call this
	// analyzer's static graph can see (confirmed doctrine gotcha, resharper-clt-step5-dead-public
	// -code) — [PublicAPI] rather than narrowing: this record IS the wire contract of POST
	// /api/health.
	[PublicAPI]
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
	// service layer).
	//
	// THE TENANT IS IN THE BODY, ONE LEVEL DOWN: `tags.project` names the project a report is filed
	// against, and a project-scoped key may only report for its own. That is now declared —
	// [TenantFrom(BodyField, "tags.project")] — and TenantEnforcementMiddleware reads the dotted path out
	// of the JSON body before the handler runs, which is what replaced the ProjectScope call that used to
	// sit below the shape validation. Two consequences worth naming: a report with no `tags.project` is
	// now refused 403 rather than answered 400 (the tenant axis decides first, and a missing tenant is a
	// refusal by construction), and this surface leaves the probe's one real blind spot — a body tenant it
	// could aim at but never verify.
	[TenantFrom(TenantSource.BodyField, "tags.project")]
	static async Task<IResult> PushAsync(HttpContext ctx, IHealthReportService health, HealthPushRequest req, CancellationToken ct)
	{
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		if (!scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Contains(ApiKeyScopes.HealthWrite, StringComparer.Ordinal))
			return Results.Forbid();

		if (req is null || string.IsNullOrWhiteSpace(req.Svc) || string.IsNullOrWhiteSpace(req.Status))
			return TypedResults.BadRequest(new ErrorResponse("svc and status are required"));

		var tags = req.Tags ?? [];

		await health.RecordPushAsync(
			new HealthReportInput(req.Svc, req.Name, tags, req.Version, req.Sha, req.BuildDate, req.Status), ct);

		return TypedResults.Ok(new OkResponse(true));
	}
}
