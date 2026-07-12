using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Data.Contract;

namespace PetBox.Data;

// Raw-SQL pass-through endpoints. Pet uses linq2db (or any other ORM) on its
// side, extracts the parameterized SQL via IExpressionQuery.GetSqlQueries,
// ships {sql, params:[{name,value,dbType?}]} JSON to petbox, petbox builds
// an ADO.NET SqliteCommand and executes (now via the shared IDataSqlService, the
// single execution path also used by the data.* MCP tools). Result rows come
// back as a JSON array of objects keyed by column name.
//
//   POST /api/data/{projectKey}/{dbName}/query → ExecuteReader; data:read
//   POST /api/data/{projectKey}/{dbName}/exec  → ExecuteNonQuery; data:write
//
// CommandTimeout defaults to 30s. Override per-request via header
// `X-PetBox-Timeout-Seconds` (capped at 300s).
//
// /exec applies a PRAGMA deny-list (in the service). SQLite error → SqliteException →
// mapped here to HTTP: SQLITE_FULL → 507, everything else → 400 with the raw message.
// Body size limits are enforced here so an oversized JSON blob can't OOM the server.
public static class QueryExecApi
{
	public const int DefaultTimeoutSeconds = 30;
	public const int MaxTimeoutSeconds = 300;
	public const long QueryBodyLimitBytes = 1L * 1024 * 1024;   // 1 MB — SQL strings are small
	public const long ExecBodyLimitBytes = 10L * 1024 * 1024;   // 10 MB — covers reasonable BLOB params

	public static void MapQueryExecEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/data/{projectKey}/{dbName}/query", QueryAsync)
			.Accepts<QueryRequest>("application/json")
			.Produces<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.Produces<SqliteErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces(StatusCodes.Status413PayloadTooLarge)
			.Produces(StatusCodes.Status507InsufficientStorage)
			.RequireAuthorization("DataRead");
		app.MapPost("/api/data/{projectKey}/{dbName}/exec", ExecAsync)
			.Accepts<QueryRequest>("application/json")
			.Produces<ExecResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.Produces<SqliteErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces(StatusCodes.Status413PayloadTooLarge)
			.Produces(StatusCodes.Status507InsufficientStorage)
			.RequireAuthorization("DataWrite");
	}

	static IResult? CheckBodySize(HttpContext ctx, long limit)
	{
		var len = ctx.Request.ContentLength;
		if (len.HasValue && len.Value > limit)
			return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
		return null;
	}

	public sealed record SqlParam(string Name, JsonElement? Value, string? DbType);
	public sealed record QueryRequest(string Sql, SqlParam[]? Params);
	public sealed record ExecResponse(int Affected);

	static async Task<IResult> QueryAsync(
		HttpContext ctx, string projectKey, string dbName, QueryRequest req,
		IDataSqlService sql, IProjectCatalog catalog, CancellationToken ct)
	{
		if (CheckBodySize(ctx, QueryBodyLimitBytes) is { } tooBig) return tooBig;
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;
		if (req is null || string.IsNullOrWhiteSpace(req.Sql))
			return Results.BadRequest(new ErrorResponse("sql is required"));

		try
		{
			var rows = await sql.QueryAsync(projectKey, dbName, req.Sql, ToArgs(req.Params), ResolveTimeoutSeconds(ctx), ct);
			return Results.Ok(rows);
		}
		catch (DataDbNotFoundException) { return Results.NotFound(new ErrorResponse("DataDb not found")); }
		catch (SqliteException ex) { return MapSqliteError(ex); }
	}

	static async Task<IResult> ExecAsync(
		HttpContext ctx, string projectKey, string dbName, QueryRequest req,
		IDataSqlService sql, IProjectCatalog catalog, CancellationToken ct)
	{
		if (CheckBodySize(ctx, ExecBodyLimitBytes) is { } tooBig) return tooBig;
		var (authOk, forbid) = await DataAuth.AuthorizeProjectAsync(ctx, projectKey, catalog, ct);
		if (!authOk) return forbid!;
		if (req is null || string.IsNullOrWhiteSpace(req.Sql))
			return Results.BadRequest(new ErrorResponse("sql is required"));

		try
		{
			var affected = await sql.ExecAsync(projectKey, dbName, req.Sql, ToArgs(req.Params), ResolveTimeoutSeconds(ctx), ct);
			return Results.Ok(new ExecResponse(affected));
		}
		catch (DataDbNotFoundException) { return Results.NotFound(new ErrorResponse("DataDb not found")); }
		catch (DeniedPragmaException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
		catch (SqliteException ex) { return MapSqliteError(ex); }
	}

	static IReadOnlyList<SqlArg> ToArgs(SqlParam[]? @params) =>
		@params is null ? [] : [.. @params.Select(p => SqlArg.FromJson(p.Name, p.Value))];

	static int ResolveTimeoutSeconds(HttpContext ctx)
	{
		if (ctx.Request.Headers.TryGetValue("X-PetBox-Timeout-Seconds", out var values)
			&& int.TryParse(values.ToString(), out var n) && n > 0)
		{
			return Math.Min(n, MaxTimeoutSeconds);
		}
		return DefaultTimeoutSeconds;
	}

	static IResult MapSqliteError(SqliteException ex)
	{
		// SQLITE_FULL = 13. See https://www.sqlite.org/rescode.html
		if (ex.SqliteErrorCode == 13)
			return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
		return Results.BadRequest(new SqliteErrorResponse(ex.Message, ex.SqliteErrorCode));
	}
}
