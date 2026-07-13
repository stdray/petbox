using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Contract;
using PetBox.Core.Data;
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
// This is a thin adapter over IDataDbCatalog: auth (ApiKey ProjectKey claim
// cross-checked against the URL) and HTTP status mapping live here; the name
// rules (regex, reserved names, quota floor), the project-existence check and
// the row+file lifecycle live in the CATALOG, so every caller — REST, pages,
// MCP db_create — gets the same rules. No db factory is opened here.
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

	public sealed record CreateDbRequest(string Name, string? Description, long? MaxPageCount);
	public sealed record DbInfo(string Name, string? Description, long MaxPageCount, DateTime CreatedAt, DateTime UpdatedAt);

	static async Task<IResult> CreateAsync(
		HttpContext ctx,
		string projectKey,
		CreateDbRequest req,
		IDataDbCatalog dataDbs,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;
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

	static async Task<IResult> ListAsync(
		HttpContext ctx,
		string projectKey,
		IDataDbCatalog dataDbs,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;

		var rows = await dataDbs.ListAsync(projectKey, ct);
		return Results.Ok(rows
			.Select(d => new DbInfo(d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt))
			.ToList());
	}

	static async Task<IResult> DeleteAsync(
		HttpContext ctx,
		string projectKey,
		string name,
		IDataDbCatalog dataDbs,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;

		// The catalog deletes the row immediately and the file best-effort (orphan cleanup
		// retries a locked file); (projectKey, name) is the address, so another project's
		// DataDb simply is not found.
		var result = await dataDbs.DeleteAsync(projectKey, name, ct);
		return result is DataDbChangeResult.Deleted
			? Results.NoContent()
			: Results.NotFound(new ErrorResponse("DataDb not found"));
	}
}
