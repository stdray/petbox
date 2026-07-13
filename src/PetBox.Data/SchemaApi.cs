using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Data.Contract;
using PetBox.Data.Schema;

namespace PetBox.Data;

// Schema management endpoints:
//   POST /api/data/{projectKey}/{dbName}/schema      — apply a named migration
//   GET  /api/data/{projectKey}/{dbName}/migrations  — list applied migrations
//
// POST flow (SchemaRunner does the heavy lifting):
//   • new name             → 200 { kind: "Applied", hash }
//   • same name + hash     → 200 { kind: "AlreadyApplied", hash } (no-op)
//   • same name diff hash  → 409 { kind: "Conflict", existingHash, providedHash }
//   • parse error / dbup   → 400 { error }
//   • bad pet input        → 400 { error }
//
// GET returns rows from __SchemaVersions in chronological order, so pets and
// the admin UI can introspect what's been applied without coupling to the
// internal table layout (the endpoint shape is the contract). The journal read
// itself lives in IDataDbCatalog.ListMigrationsAsync — no db factory here.
public static class SchemaApi
{
	public static void MapSchemaEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/data/{projectKey}/{dbName}/schema", ApplyAsync)
			.Accepts<SchemaApplyRequest>("application/json")
			.Produces<SchemaApplyResponse>()
			.Produces<SchemaApplyResponse>(StatusCodes.Status409Conflict)
			.Produces<SchemaFailedResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("DataSchema");
		app.MapGet("/api/data/{projectKey}/{dbName}/migrations", ListMigrationsAsync)
			.Produces<List<MigrationEntry>>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("DataRead");
	}

	public sealed record SchemaApplyRequest(string Name, string Sql);
	public sealed record SchemaApplyResponse(string Kind, string Hash, string? ExistingHash);
	public sealed record MigrationEntry(long Id, string ScriptName, DateTime Applied, string Hash);

	static async Task<IResult> ApplyAsync(
		HttpContext ctx,
		string projectKey,
		string dbName,
		SchemaApplyRequest req,
		IDataSqlService sql,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;
		if (req is null || string.IsNullOrWhiteSpace(req.Name))
			return Results.BadRequest(new ErrorResponse("name is required"));
		if (req.Sql is null)
			return Results.BadRequest(new ErrorResponse("sql is required"));

		// The service resolves the DataDb row and opens its quota'd connection (migration
		// SQL writes, and max_page_count is per-connection) — same path as the MCP tool.
		SchemaApplyResult result;
		try { result = await sql.ApplySchemaAsync(projectKey, dbName, req.Name, req.Sql, ct); }
		catch (DataDbNotFoundException) { return Results.NotFound(new ErrorResponse("DataDb not found")); }

		var payload = new SchemaApplyResponse(result.Kind.ToString(), result.Hash, result.ExistingHash);
		return result.Kind switch
		{
			SchemaApplyKind.Applied => Results.Ok(payload),
			SchemaApplyKind.AlreadyApplied => Results.Ok(payload),
			SchemaApplyKind.Conflict => Results.Conflict(payload),
			SchemaApplyKind.Failed => Results.BadRequest(new SchemaFailedResponse(result.Error, result.Hash)),
			_ => Results.StatusCode(500),
		};
	}

	static async Task<IResult> ListMigrationsAsync(
		HttpContext ctx,
		string projectKey,
		string dbName,
		IDataDbCatalog dataDbs,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;

		// The catalog proves the (projectKey, dbName) address, opens the quota'd connection
		// and reads __SchemaVersions; null = not this project's DataDb, [] = no journal yet.
		var rows = await dataDbs.ListMigrationsAsync(projectKey, dbName, ct);
		if (rows is null) return Results.NotFound(new ErrorResponse("DataDb not found"));

		return Results.Ok(rows
			.Select(m => new MigrationEntry(m.Id, m.ScriptName, m.Applied, m.Hash))
			.ToList());
	}
}
