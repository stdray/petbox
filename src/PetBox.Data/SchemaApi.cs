using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
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
// internal table layout (the endpoint shape is the contract).
public static class SchemaApi
{
	public static void MapSchemaEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/data/{projectKey}/{dbName}/schema", ApplyAsync)
			.RequireAuthorization("DataSchema");
		app.MapGet("/api/data/{projectKey}/{dbName}/migrations", ListMigrationsAsync)
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
		PetBoxDb db,
		IDataDbFactory factory,
		SchemaRunner runner,
		CancellationToken ct)
	{
		if (!DataAuth.AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;
		if (req is null || string.IsNullOrWhiteSpace(req.Name))
			return Results.BadRequest(new { error = "name is required" });
		if (req.Sql is null)
			return Results.BadRequest(new { error = "sql is required" });

		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) return Results.NotFound(new { error = "DataDb not found" });

		var cs = factory.GetConnectionString(projectKey, dbName);
		var result = runner.Apply(cs, req.Name, req.Sql);

		var payload = new SchemaApplyResponse(result.Kind.ToString(), result.Hash, result.ExistingHash);
		return result.Kind switch
		{
			SchemaApplyKind.Applied => Results.Ok(payload),
			SchemaApplyKind.AlreadyApplied => Results.Ok(payload),
			SchemaApplyKind.Conflict => Results.Conflict(payload),
			SchemaApplyKind.Failed => Results.BadRequest(new { error = result.Error, hash = result.Hash }),
			_ => Results.StatusCode(500),
		};
	}

	static async Task<IResult> ListMigrationsAsync(
		HttpContext ctx,
		string projectKey,
		string dbName,
		PetBoxDb db,
		IDataDbFactory factory,
		CancellationToken ct)
	{
		if (!DataAuth.AuthorizeProject(ctx, projectKey, out var forbid)) return forbid;

		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) return Results.NotFound(new { error = "DataDb not found" });

		var cs = factory.GetConnectionString(projectKey, dbName);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);

		// __SchemaVersions may not exist yet if no migrations have been applied.
		await using var existsCmd = conn.CreateCommand();
		existsCmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{SchemaRunner.JournalTableName}'";
		var exists = await existsCmd.ExecuteScalarAsync(ct) is not null;
		if (!exists) return Results.Ok(Array.Empty<MigrationEntry>());

		await using var cmd = conn.CreateCommand();
		cmd.CommandText = $"SELECT SchemaVersionID, ScriptName, Applied, Hash FROM {SchemaRunner.JournalTableName} ORDER BY SchemaVersionID";
		await using var reader = await cmd.ExecuteReaderAsync(ct);
		var entries = new List<MigrationEntry>();
		while (await reader.ReadAsync(ct))
		{
			entries.Add(new MigrationEntry(
				Id: reader.GetInt64(0),
				ScriptName: reader.GetString(1),
				Applied: reader.GetDateTime(2),
				Hash: reader.GetString(3)));
		}
		return Results.Ok(entries);
	}
}
