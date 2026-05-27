using System.Security.Cryptography;
using System.Text;
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
		var requestTags = (tags ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.ToList();

		var wsTag = $"ws:{workspaceKey}";
		if (!requestTags.Any(t => string.Equals(t, wsTag, StringComparison.OrdinalIgnoreCase)))
			requestTags.Add(wsTag);

		var configDb = configFactory.GetConfigDb(workspaceKey);
		var bindings = configDb.Bindings.ToList();

		try
		{
			var result = ResolvePipeline.Resolve(path, requestTags, bindings);
			if (result is null)
				return Results.NotFound(new { error = "no matching binding" });

			var etag = ComputeETag(path, result);
			var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
			if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
			{
				context.Response.Headers.ETag = etag;
				return Results.StatusCode(StatusCodes.Status304NotModified);
			}

			context.Response.Headers.ETag = etag;
			return Results.Ok(new { path, value = result });
		}
		catch (AmbiguousConfigException ex)
		{
			return Results.Conflict(new { error = "ambiguous", path = ex.Path, candidates = ex.CandidateBindingIds });
		}
	}

	// Strong validator over the resolved value. Path is included so clients caching
	// across paths can't collide; value is the canonical resolved string. Tag set isn't
	// part of the ETag — same (path, value) tuple is cache-equivalent regardless of which
	// binding produced it.
	static string ComputeETag(string path, string value)
	{
		Span<byte> hash = stackalloc byte[32];
		var bytes = Encoding.UTF8.GetBytes($"{path}\0{value}");
		SHA256.HashData(bytes, hash);
		return $"\"{Convert.ToHexStringLower(hash[..16])}\"";
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
