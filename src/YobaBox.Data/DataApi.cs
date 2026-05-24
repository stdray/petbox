using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Data;

public static class DataApi
{
	public static void MapDataEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/data/{table}", GetRows).RequireAuthorization("DataRead");
		app.MapPost("/api/data/{table}", InsertRow).RequireAuthorization("DataWrite");
		app.MapDelete("/api/data/{table}", DeleteRow).RequireAuthorization("DataWrite");
	}

	static async Task<IResult> GetRows(HttpContext ctx, YobaBoxDb db, string table)
	{
		var dt = await db.DataTables
			.FirstOrDefaultAsync((DataTable t) => t.Name == table);

		if (dt is null)
			return Results.NotFound(new { error = "table not found" });

		return Results.Ok(new
		{
			table = dt.Name,
			columns = dt.Columns,
			read = dt.Read,
			write = dt.Write,
			delete = dt.Delete,
			rows = Array.Empty<object>(),
		});
	}

	static async Task<IResult> InsertRow(HttpContext ctx, YobaBoxDb db, string table)
	{
		var dt = await db.DataTables
			.FirstOrDefaultAsync((DataTable t) => t.Name == table);

		if (dt is null)
			return Results.NotFound(new { error = "table not found" });
		if (!dt.Write)
			return Results.BadRequest(new { error = "write disabled" });

		return Results.Ok(new { created = true });
	}

	static async Task<IResult> DeleteRow(HttpContext ctx, YobaBoxDb db, string table)
	{
		var dt = await db.DataTables
			.FirstOrDefaultAsync((DataTable t) => t.Name == table);

		if (dt is null)
			return Results.NotFound(new { error = "table not found" });
		if (!dt.Delete)
			return Results.BadRequest(new { error = "delete disabled" });

		return Results.Ok(new { deleted = true });
	}
}
