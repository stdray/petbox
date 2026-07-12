using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;

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
// The project itself must exist in PetBoxDb.Projects — petbox is the source
// of truth for project identity. ApiKey carries ProjectKey claim; we cross-
// check it against the URL to prevent cross-project access via crafted URLs.
public static partial class DataDbsApi
{
	// Matches the SQLite reserved-name spec we settled on in plan:
	//   - starts with a-z
	//   - followed by a-z, 0-9, '_' or '-'
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex DbNameRegex();

	static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"__schema_versions",
	};

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
		PetBoxDb db,
		IDataDbFactory factory,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;
		if (req is null || string.IsNullOrWhiteSpace(req.Name))
			return Results.BadRequest(new ErrorResponse("name is required"));
		if (!DbNameRegex().IsMatch(req.Name))
			return Results.BadRequest(new ErrorResponse("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$"));
		if (ReservedNames.Contains(req.Name))
			return Results.BadRequest(new ErrorResponse($"'{req.Name}' is reserved"));

		var project = await db.Projects.FirstOrDefaultAsync((Project p) => p.Key == projectKey, ct);
		if (project is null) return Results.NotFound(new ErrorResponse("project not found"));

		var exists = await db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == projectKey && d.Name == req.Name, ct);
		if (exists) return Results.Conflict(new ErrorResponse($"DataDb '{req.Name}' already exists"));

		var maxPageCount = req.MaxPageCount ?? DataDbFactory.DefaultMaxPageCount;
		if (maxPageCount < 1024)
			return Results.BadRequest(new ErrorResponse("maxPageCount must be >= 1024 (4 MB at 4KB pages)"));

		await factory.CreateAsync(projectKey, req.Name, maxPageCount, ct);

		var now = DateTime.UtcNow;
		await db.InsertAsync(new DataDb
		{
			ProjectKey = projectKey,
			Name = req.Name,
			Description = req.Description,
			MaxPageCount = maxPageCount,
			CreatedAt = now,
			UpdatedAt = now,
		}, token: ct);

		return Results.Created(
			$"/api/data/{projectKey}/dbs/{req.Name}",
			new DbInfo(req.Name, req.Description, maxPageCount, now, now));
	}

	static async Task<IResult> ListAsync(
		HttpContext ctx,
		string projectKey,
		PetBoxDb db,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;

		var rows = await db.DataDbs
			.Where(d => d.ProjectKey == projectKey)
			.OrderBy(d => d.Name)
			.Select(d => new DbInfo(d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt))
			.ToListAsync(ct);

		return Results.Ok(rows);
	}

	static async Task<IResult> DeleteAsync(
		HttpContext ctx,
		string projectKey,
		string name,
		PetBoxDb db,
		IDataDbFactory factory,
		IProjectCatalog catalog,
		CancellationToken ct)
	{
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;

		var deleted = await db.DataDbs
			.Where(d => d.ProjectKey == projectKey && d.Name == name)
			.DeleteAsync(ct);

		if (deleted == 0)
			return Results.NotFound(new ErrorResponse("DataDb not found"));

		// Best-effort file removal. If locked (in-flight query), orphan cleanup
		// service retries on its next tick — the metadata row is gone so the
		// (projectKey, name) slot is free immediately.
		factory.TryDelete(projectKey, name);

		return Results.NoContent();
	}

}
