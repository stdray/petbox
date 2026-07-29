using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Data.Contract;

namespace PetBox.Data;

// Lifecycle endpoints for per-(project, name) DataDbs:
//   POST   /api/data/{projectKey}/dbs        — create
//   GET    /api/data/{projectKey}/dbs        — list
//   DELETE /api/data/{projectKey}/dbs/{name} — delete (row immediately; file
//                                              best-effort, orphan cleanup
//                                              service handles locked files)
//
// All endpoints require `data:schema` scope EXCEPT GET which uses `data:read`
// (listing is harmless reconnaissance).
//
// This is a thin adapter over IDataDbCatalog: HTTP status mapping lives here; the name
// rules (regex, reserved names, quota floor), the project-existence check and
// the row+file lifecycle live in the CATALOG, so every caller — REST, pages,
// MCP db_create — gets the same rules. No db factory is opened here.
//
// THE TENANT IS DECLARED, NOT CHECKED HERE (spec authz-scope-declaration): every route below names
// its project in the path, so each handler carries [TenantFrom(Route, "projectKey")] and
// TenantEnforcementMiddleware refuses a caller not entitled to it BEFORE the handler runs. That is
// what replaced DataAuth.AuthorizeProjectAsync — the same ProjectScope decision (claim identity +
// sandbox containment), reached through ITenantAuthorizer, one call earlier, and now also ahead of
// argument binding: a foreign tenant can no longer be answered "400 name is required" first.
public static class DataDbsApi
{
	public static void MapDataDbsEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/data/{projectKey}/dbs", CreateAsync)
			.Accepts<CreateDbRequest>("application/json")
			.Produces<DbInfo>(StatusCodes.Status201Created)
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.Produces<ErrorResponse>(StatusCodes.Status409Conflict)
			.RequireAuthorization("DataSchema");
		app.MapGet("/api/data/{projectKey}/dbs", ListAsync)
			.Produces<List<DbInfo>>()
			.RequireAuthorization("DataRead");
		app.MapDelete("/api/data/{projectKey}/dbs/{name}", DeleteAsync)
			.Produces(StatusCodes.Status204NoContent)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("DataSchema");
	}

	private sealed record CreateDbRequest(string Name, string? Description, long? MaxPageCount);
	public sealed record DbInfo(string Name, string? Description, long MaxPageCount, DateTime CreatedAt, DateTime UpdatedAt);

	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> CreateAsync(
		string projectKey,
		CreateDbRequest req,
		IDataDbCatalog dataDbs,
		CancellationToken ct)
	{
		if (req is null)
			return Results.BadRequest(new ErrorResponse("name is required"));

		// Name/quota/uniqueness rules are the catalog's, not ours — see the class comment.
		var result = await dataDbs.CreateAsync(projectKey, req.Name, req.Description, req.MaxPageCount, ct);
		return result switch
		{
			DataDbChangeResult.Created c => Results.Created(
				$"/api/data/{projectKey}/dbs/{c.Db.Name}",
				new DbInfo(c.Db.Name, c.Db.Description, c.Db.MaxPageCount, c.Db.CreatedAt, c.Db.UpdatedAt)),
			DataDbChangeResult.NotFound => Results.NotFound(new ErrorResponse("project not found")),
			DataDbChangeResult.Conflict k => Results.Conflict(new ErrorResponse(k.Reason)),
			DataDbChangeResult.Refused r => Results.BadRequest(new ErrorResponse(r.Reason)),
			_ => Results.StatusCode(StatusCodes.Status500InternalServerError),
		};
	}

	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> ListAsync(
		string projectKey,
		IDataDbCatalog dataDbs,
		CancellationToken ct)
	{
		var rows = await dataDbs.ListAsync(projectKey, ct);
		return Results.Ok(rows
			.Select(d => new DbInfo(d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt))
			.ToList());
	}

	[TenantFrom(TenantSource.Route, "projectKey")]
	static async Task<IResult> DeleteAsync(
		string projectKey,
		string name,
		IDataDbCatalog dataDbs,
		CancellationToken ct)
	{
		// The catalog deletes the row immediately and the file best-effort (orphan cleanup
		// retries a locked file); (projectKey, name) is the address, so another project's
		// DataDb simply is not found.
		var result = await dataDbs.DeleteAsync(projectKey, name, ct);
		return result is DataDbChangeResult.Deleted
			? Results.NoContent()
			: Results.NotFound(new ErrorResponse("DataDb not found"));
	}
}
