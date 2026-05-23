using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Config;

public static class ConfigApi
{
	public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/config", Resolve).RequireAuthorization("ConfigRead");
		app.MapPost("/api/config", Create).RequireAuthorization("ConfigWrite");
		app.MapDelete("/api/config", Delete).RequireAuthorization("ConfigWrite");
	}

	static IResult Resolve(HttpContext context, YobaBoxDb db, string path, string tags)
	{
		var requestTags = tags
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		var bindings = db.ConfigBindings.ToList();
		var result = ResolvePipeline.Resolve(path, requestTags, bindings);

		return result is null
			? Results.NotFound(new { error = "no matching binding" })
			: Results.Ok(new { path, value = result });
	}

	static async Task<IResult> Create(HttpContext context, YobaBoxDb db, ConfigBinding binding)
	{
		binding = binding with
		{
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = DateTime.UtcNow,
		};

		var id = Convert.ToInt64(await db.InsertWithIdentityAsync(binding));
		return Results.Ok(new { id, binding.Path, binding.Tags });
	}

	static async Task<IResult> Delete(HttpContext context, YobaBoxDb db, string path, string tags)
	{
		var deleted = await db.ConfigBindings
			.Where(b => b.Path == path && b.Tags == tags)
			.DeleteAsync();

		return deleted > 0
			? Results.Ok(new { deleted = true })
			: Results.NotFound(new { error = "binding not found" });
	}
}
