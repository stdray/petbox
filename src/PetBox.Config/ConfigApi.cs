using System.Security.Cryptography;
using System.Text;
using LinqToDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Config;

public sealed record ConfigBindingDto(string Path, string Value, string Tags, BindingKind Kind = BindingKind.Plain);

public static class ConfigApi
{
	public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/config/{workspaceKey}/bindings", Create).RequireAuthorization("ConfigWrite");
		app.MapDelete("/api/config/{workspaceKey}/bindings", Delete).RequireAuthorization("ConfigWrite");

		// Canonical read API (yobaconf-compatible bulk resolve). The published config clients
		// (@stdray/petbox-client, PetBox.Client.Config) target this shape: GET /v1/conf?<tags>
		// with optional ?template=, header X-YobaConf-ApiKey, ETag/If-None-Match.
		app.MapGet("/v1/conf", Conf).RequireAuthorization("ConfigRead");
	}

	// Resolves every config path visible to the calling API key's project, shaped by template.
	// Workspace is derived from the key's project (ApiKey is project-scoped); tags come from the
	// query string plus auto ws:/project: tags.
	static IResult Conf(HttpContext context, PetBoxDb db, IConfigDbFactory configFactory, ISecretEncryptor encryptor)
	{
		var projectKey = context.User.FindFirst("project")?.Value;
		if (string.IsNullOrEmpty(projectKey))
			return Results.Unauthorized();

		var project = db.Projects.FirstOrDefault(p => p.Key == projectKey);
		if (project is null)
			return Results.NotFound(new { error = "project not found", project = projectKey });

		var workspaceKey = project.WorkspaceKey;

		string? template = null;
		var requestTags = new List<string> { $"ws:{workspaceKey}", $"project:{projectKey}" };
		foreach (var (key, vals) in context.Request.Query)
		{
			if (string.Equals(key, "template", StringComparison.OrdinalIgnoreCase))
			{
				template = vals.ToString();
				continue;
			}
			requestTags.Add($"{key}:{vals}");
		}

		var configDb = configFactory.GetConfigDb(workspaceKey);
		var bindings = configDb.Bindings.ToList();

		IReadOnlyList<ResolveMatch> matches;
		try
		{
			matches = ResolvePipeline.ResolveAll(requestTags, bindings);
		}
		catch (AmbiguousConfigException ex)
		{
			return Results.Conflict(new { error = "ambiguous", path = ex.Path, candidates = ex.CandidateBindingIds });
		}

		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var m in matches)
			values[m.Binding.Path] = ResolveValue(m.Binding, encryptor);

		var etag = ComputeSetETag(values);
		var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
		if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
		{
			context.Response.Headers.ETag = etag;
			return Results.StatusCode(StatusCodes.Status304NotModified);
		}

		context.Response.Headers.ETag = etag;

		// dotenv is a text/plain body (KEY=value lines), not a JSON shape — so consumers can use
		// `docker --env-file`, compose `env_file:`, shell sourcing or a dotenv lib with no bespoke
		// PetBox client. Every other template serializes to JSON via Shape.
		if (string.Equals(template, "dotenv", StringComparison.OrdinalIgnoreCase))
			return Results.Text(ConfigTemplates.Dotenv(values), "text/plain; charset=utf-8");

		return Results.Ok(ConfigTemplates.Shape(values, template));
	}

	static string ResolveValue(ConfigBinding b, ISecretEncryptor encryptor)
	{
		if (b.Kind == BindingKind.Secret && encryptor.IsAvailable
			&& b.Ciphertext is not null && b.Iv is not null && b.AuthTag is not null)
		{
			try { return encryptor.Decrypt(b.Ciphertext, b.Iv, b.AuthTag); }
			catch { return string.Empty; }
		}
		return b.Value;
	}

	// ETag over the whole resolved set: sorted path\0value lines, hashed. Same (set) → same tag.
	static string ComputeSetETag(IReadOnlyDictionary<string, string> values)
	{
		var sb = new StringBuilder();
		foreach (var kv in values.OrderBy(k => k.Key, StringComparer.Ordinal))
			sb.Append(kv.Key).Append('\0').Append(kv.Value).Append('\n');
		Span<byte> hash = stackalloc byte[32];
		SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), hash);
		return $"\"{Convert.ToHexStringLower(hash[..16])}\"";
	}

	static async Task<IResult> Create(HttpContext context, IConfigDbFactory configFactory, string workspaceKey, ConfigBindingDto dto)
	{
		if (string.IsNullOrWhiteSpace(dto.Path))
			return Results.BadRequest(new { error = "path is required" });
		if (!dto.Tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
			return Results.BadRequest(new { error = $"Tags must include 'ws:{workspaceKey}'" });

		var now = DateTime.UtcNow;
		var value = dto.Value ?? string.Empty;
		var binding = new ConfigBinding
		{
			Path = dto.Path,
			Value = value,
			Tags = dto.Tags,
			Kind = dto.Kind,
			Version = 1,
			ContentHash = BindingContentHash.Compute(dto.Path, dto.Tags, dto.Kind, value, null),
			CreatedAt = now,
			UpdatedAt = now,
		};

		var configDb = configFactory.GetConfigDb(workspaceKey);
#pragma warning disable CA2016
		var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(binding));
#pragma warning restore CA2016
		return Results.Ok(new { id, binding.Path, binding.Tags });
	}

	// Soft-delete: mark IsDeleted=1, keep the row. Resolve filters it out.
	// UI's history page can offer "Undelete" for the last deleted version.
	static async Task<IResult> Delete(HttpContext context, IConfigDbFactory configFactory, string workspaceKey, string path, string tags)
	{
		var configDb = configFactory.GetConfigDb(workspaceKey);
		var now = DateTime.UtcNow;
		var deleted = await configDb.Bindings
			.Where(b => b.Path == path && b.Tags == tags && !b.IsDeleted)
			.Set(b => b.IsDeleted, true)
			.Set(b => b.DeletedAt, (DateTime?)now)
			.Set(b => b.UpdatedAt, now)
			.UpdateAsync();

		return deleted > 0
			? Results.Ok(new { deleted = true })
			: Results.NotFound(new { error = "binding not found" });
	}
}
