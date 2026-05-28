using System.Text.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Data;

// Raw-SQL pass-through endpoints. Pet uses linq2db (or any other ORM) on its
// side, extracts the parameterized SQL via IExpressionQuery.GetSqlQueries,
// ships {sql, params:[{name,value,dbType?}]} JSON to yobabox, yobabox builds
// an ADO.NET SqliteCommand and executes. Result rows come back as a JSON
// array of objects keyed by column name.
//
//   POST /api/data/{projectKey}/{dbName}/query → ExecuteReader; data:read
//   POST /api/data/{projectKey}/{dbName}/exec  → ExecuteNonQuery; data:write
//
// CommandTimeout defaults to 30s. Override per-request via header
// `X-Yobabox-Timeout-Seconds` (capped at 300s) — long-running queries are
// the pet's responsibility.
//
// /exec applies a PRAGMA deny-list (cheaper to maintain than allow-list as
// SQLite ships new PRAGMAs each release). Default-allow keeps the
// "raw SQL pass-through" promise; only specifically dangerous PRAGMAs that
// could escape the DB file or corrupt shared state are blocked.
//
// SQLite error → SqliteException → mapped to HTTP code:
//   • SQLITE_FULL (quota exceeded) → 507 Insufficient Storage
//   • everything else              → 400 Bad Request with raw SQLite message
//
// Body size limits are configured at endpoint registration time so a pet
// uploading a 200MB JSON blob can't OOM the server before SQLite gets a turn.
public static class QueryExecApi
{
	public const int DefaultTimeoutSeconds = 30;
	public const int MaxTimeoutSeconds = 300;
	public const long QueryBodyLimitBytes = 1L * 1024 * 1024;   // 1 MB — SQL strings are small
	public const long ExecBodyLimitBytes = 10L * 1024 * 1024;   // 10 MB — covers reasonable BLOB params

	// PRAGMAs that escape the DB file or corrupt shared state. Any /exec body
	// whose first non-whitespace token is `PRAGMA` and whose pragma name is
	// in this set is rejected.
	static readonly HashSet<string> PragmaDenyList = new(StringComparer.OrdinalIgnoreCase)
	{
		"writable_schema",
		"temp_store_directory",
		"data_store_directory",
		"trusted_schema",
	};

	public static void MapQueryExecEndpoints(this IEndpointRouteBuilder app)
	{
		// Per-endpoint body size limits are enforced inside the handler via
		// Request.ContentLength + a hand-rolled check, because minimal APIs
		// don't expose [RequestSizeLimit]. A client that lies about
		// Content-Length still gets stopped by Kestrel's default limit.
		app.MapPost("/api/data/{projectKey}/{dbName}/query", QueryAsync)
			.RequireAuthorization("DataRead");
		app.MapPost("/api/data/{projectKey}/{dbName}/exec", ExecAsync)
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
		HttpContext ctx,
		string projectKey,
		string dbName,
		QueryRequest req,
		YobaBoxDb db,
		IDataDbFactory factory,
		CancellationToken ct)
	{
		if (CheckBodySize(ctx, QueryBodyLimitBytes) is { } tooBig) return tooBig;
		if (!DataAuth.AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (req is null || string.IsNullOrWhiteSpace(req.Sql))
			return Results.BadRequest(new { error = "sql is required" });

		var dbRow = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (dbRow is null) return Results.NotFound(new { error = "DataDb not found" });

		var cs = factory.GetConnectionString(projectKey, dbName);
		var timeout = ResolveTimeoutSeconds(ctx);

		try
		{
			await using var conn = new SqliteConnection(cs);
			await conn.OpenAsync(ct);
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = req.Sql;
			cmd.CommandTimeout = timeout;
			BindParameters(cmd, req.Params);

			var rows = new List<Dictionary<string, object?>>();
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			var fieldCount = reader.FieldCount;
			while (await reader.ReadAsync(ct))
			{
				var row = new Dictionary<string, object?>(fieldCount, StringComparer.Ordinal);
				for (var i = 0; i < fieldCount; i++)
					row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
				rows.Add(row);
			}
			return Results.Ok(rows);
		}
		catch (SqliteException ex) { return MapSqliteError(ex); }
	}

	static async Task<IResult> ExecAsync(
		HttpContext ctx,
		string projectKey,
		string dbName,
		QueryRequest req,
		YobaBoxDb db,
		IDataDbFactory factory,
		CancellationToken ct)
	{
		if (CheckBodySize(ctx, ExecBodyLimitBytes) is { } tooBig) return tooBig;
		if (!DataAuth.AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (req is null || string.IsNullOrWhiteSpace(req.Sql))
			return Results.BadRequest(new { error = "sql is required" });

		if (IsBlockedPragma(req.Sql, out var deniedName))
			return Results.BadRequest(new { error = $"PRAGMA {deniedName} is not allowed" });

		var dbRow = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (dbRow is null) return Results.NotFound(new { error = "DataDb not found" });

		var cs = factory.GetConnectionString(projectKey, dbName);
		var timeout = ResolveTimeoutSeconds(ctx);

		try
		{
			await using var conn = new SqliteConnection(cs);
			await conn.OpenAsync(ct);
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = req.Sql;
			cmd.CommandTimeout = timeout;
			BindParameters(cmd, req.Params);

			var affected = await cmd.ExecuteNonQueryAsync(ct);
			return Results.Ok(new ExecResponse(affected));
		}
		catch (SqliteException ex) { return MapSqliteError(ex); }
	}

	static int ResolveTimeoutSeconds(HttpContext ctx)
	{
		if (ctx.Request.Headers.TryGetValue("X-Yobabox-Timeout-Seconds", out var values)
			&& int.TryParse(values.ToString(), out var n) && n > 0)
		{
			return Math.Min(n, MaxTimeoutSeconds);
		}
		return DefaultTimeoutSeconds;
	}

	static void BindParameters(SqliteCommand cmd, SqlParam[]? @params)
	{
		if (@params is null) return;
		foreach (var p in @params)
		{
			var dbp = cmd.CreateParameter();
			dbp.ParameterName = p.Name;
			dbp.Value = ConvertJsonValue(p.Value);
			cmd.Parameters.Add(dbp);
		}
	}

	static object ConvertJsonValue(JsonElement? je)
	{
		if (je is null) return DBNull.Value;
		var el = je.Value;
		return el.ValueKind switch
		{
			JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.Number when el.TryGetInt64(out var l) => l,
			JsonValueKind.Number => el.GetDouble(),
			JsonValueKind.String => el.GetString() ?? (object)DBNull.Value,
			_ => el.GetRawText(), // arrays/objects → store as JSON text
		};
	}

	static bool IsBlockedPragma(string sql, out string? name)
	{
		name = null;
		var trimmed = sql.TrimStart();
		if (!trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)) return false;
		var rest = trimmed.AsSpan(6).TrimStart();
		var end = 0;
		while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_')) end++;
		if (end == 0) return false;
		var pragmaName = rest[..end].ToString();
		if (PragmaDenyList.Contains(pragmaName)) { name = pragmaName; return true; }
		return false;
	}

	static IResult MapSqliteError(SqliteException ex)
	{
		// SQLITE_FULL = 13. See https://www.sqlite.org/rescode.html
		if (ex.SqliteErrorCode == 13)
			return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
		return Results.BadRequest(new { error = ex.Message, code = ex.SqliteErrorCode });
	}
}
