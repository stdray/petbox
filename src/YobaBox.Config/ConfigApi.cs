using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using YobaBox.Config.Data;
using YobaBox.Core.Models;

namespace YobaBox.Config;

public sealed record ConfigBindingDto(string Path, string Value, string Tags, BindingKind Kind = BindingKind.Plain);

public static class ConfigApi
{
	public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/config/{workspaceKey}/resolve", Resolve).RequireAuthorization("ConfigRead");
		app.MapPost("/api/config/{workspaceKey}/bindings", Create).RequireAuthorization("ConfigWrite");
		app.MapDelete("/api/config/{workspaceKey}/bindings", Delete).RequireAuthorization("ConfigWrite");
	}

	static IResult Resolve(HttpContext context, IConfigDbFactory configFactory, string workspaceKey, string path, string tags)
	{
		var requestTags = tags
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		var configDb = configFactory.GetConfigDb(workspaceKey);
		var bindings = configDb.Bindings.ToList();
		var result = ResolvePipeline.Resolve(path, requestTags, bindings);

		return result is null
			? Results.NotFound(new { error = "no matching binding" })
			: Results.Ok(new { path, value = result });
	}

	static async Task<IResult> Create(HttpContext context, IConfigDbFactory configFactory, string workspaceKey, ConfigBindingDto dto)
	{
		if (string.IsNullOrWhiteSpace(dto.Path))
			return Results.BadRequest(new { error = "path is required" });
		if (!dto.Tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
			return Results.BadRequest(new { error = $"Tags must include 'ws:{workspaceKey}'" });

		var now = DateTime.UtcNow;
		var binding = new ConfigBinding
		{
			Path = dto.Path,
			Value = dto.Value ?? string.Empty,
			Tags = dto.Tags,
			Kind = dto.Kind,
			CreatedAt = now,
			UpdatedAt = now,
		};

		var configDb = configFactory.GetConfigDb(workspaceKey);
#pragma warning disable CA2016
		var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(binding));
#pragma warning restore CA2016
		return Results.Ok(new { id, binding.Path, binding.Tags });
	}

	static async Task<IResult> Delete(HttpContext context, IConfigDbFactory configFactory, string workspaceKey, string path, string tags)
	{
		var configDb = configFactory.GetConfigDb(workspaceKey);
		var deleted = await configDb.Bindings
			.Where(b => b.Path == path && b.Tags == tags)
			.DeleteAsync();

		return deleted > 0
			? Results.Ok(new { deleted = true })
			: Results.NotFound(new { error = "binding not found" });
	}
}
