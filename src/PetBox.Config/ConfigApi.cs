using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Config.Contract;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Models;

namespace PetBox.Config;

public sealed record ConfigBindingDto(string Path, string Value, string Tags, BindingKind Kind = BindingKind.Plain);

// THE TENANT OF THE BINDING ROUTES IS A WORKSPACE, AND IT IS DECLARED AS ONE — read this before
// touching either declaration.
//
// A config:write key is project-scoped like every other ApiKey, and the "ConfigWrite" policy only
// proves the SCOPE is present, so the {workspaceKey} segment is attacker-controlled: without a tenant
// check any config:write key could write or soft-delete bindings in ANY workspace. What used to close
// that was AuthorizeWorkspaceAsync in this file (project claim -> Project.WorkspaceKey -> compare,
// wildcard "*" passes). It is deleted, and its rule is now
// [TenantFrom(Route, "workspaceKey", TenantKind.Workspace)] answered by
// ITenantAuthorizer.KeyOnWorkspaceAsync — which is the SAME rule, verbatim: same claim, same
// Project.WorkspaceKey read (ProjectCatalog.WorkspaceKeyOfAsync vs
// ConfigDirectory.GetProjectWorkspaceAsync are the same query on the same column), same wildcard
// pass, and the same non-treatment of sandboxOnly for a workspace target.
//
// WHAT THIS IS DELIBERATELY *NOT*: `provisioning`. The three MCP config verbs
// (config_binding_upsert/search/delta) declared that class and consequently SERVE a foreign
// workspace — AuthzCrossTenantTests records all three, and records that "the REST twin denies". That
// asymmetry is a known defect with its own card, owned by the maintainer; it is NOT a licence to
// bring REST into line by opening it. Declaring these two `provisioning` would turn a measured 403
// into cross-tenant config WRITE, i.e. it would use this wave to widen the hole the wave exists to
// close. The REST refusal is the correct half of the divergence and it is kept.
public static class ConfigApi
{
	public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapPost("/api/config/{workspaceKey}/bindings", Create)
			.Accepts<ConfigBindingDto>("application/json")
			.Produces<ConfigBindingCreatedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.RequireAuthorization("ConfigWrite");
		app.MapDelete("/api/config/{workspaceKey}/bindings", Delete)
			.Produces<DeletedResponse>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ConfigWrite");

		// Canonical read API (yobaconf-compatible bulk resolve). The published config clients
		// (@stdray/petbox-client, PetBox.Client.Config) target this shape: GET /v1/conf?<tags>
		// with optional ?template=, header X-YobaConf-ApiKey, ETag/If-None-Match.
		app.MapGet("/v1/conf", Conf)
			.Produces<Dictionary<string, object>>()
			.Produces<ConfigProjectNotFoundResponse>(StatusCodes.Status404NotFound)
			.Produces<ConfigAmbiguousResponse>(StatusCodes.Status409Conflict)
			.RequireAuthorization("ConfigRead");
	}

	// Resolves every config path visible to the calling API key's project, shaped by template.
	// Workspace is derived from the key's project (ApiKey is project-scoped); tags come from the
	// query string plus auto ws:/project: tags.
	//
	// The route names no tenant, so the tenant is the CALLER's own — [TenantFrom(CallerDefault)] — and
	// TenantEnforcementMiddleware refuses a caller whose own project does not resolve. That is what
	// replaced the `Results.Unauthorized()` below it.
	//
	// It also collapses a second reading of "which project is this key's own?". CallerTenant is
	// documented as the ONLY one (the MCP plane asks it through ModuleMcp.DefaultProjectOf), and this
	// handler had its own copy that read the raw `project` claim — the two disagree on exactly the keys
	// CallerTenant's comment names: a cross-project key, whose claim says "*" and whose
	// `project_default` says otherwise. The old copy then resolved config for a project literally named
	// "*" and 404'd; asking CallerTenant gives such a key the default project it was minted with, which
	// is both what the PEP authorized and what the same key already gets over MCP.
	[TenantFrom(TenantSource.CallerDefault)]
	static async Task<IResult> Conf(HttpContext context, IConfigDirectory config, ISecretEncryptor encryptor)
	{
		// Cannot be null once the PEP has allowed the call (an unresolved CallerDefault tenant is a
		// refusal); the narrowing keeps it fail-closed if this surface's declaration ever changes.
		var projectKey = CallerTenant.DefaultProjectOf(context.User);
		if (projectKey is null) return Results.Unauthorized();

		var workspaceKey = await config.GetProjectWorkspaceAsync(projectKey, context.RequestAborted);
		if (workspaceKey is null)
			return Results.NotFound(new ConfigProjectNotFoundResponse("project not found", projectKey));

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

		IReadOnlyList<ResolveMatch> matches;
		try
		{
			matches = await config.ResolveAllAsync(workspaceKey, requestTags, context.RequestAborted);
		}
		catch (AmbiguousConfigException ex)
		{
			return Results.Conflict(new ConfigAmbiguousResponse("ambiguous", ex.Path, ex.CandidateBindingIds));
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

	[TenantFrom(TenantSource.Route, "workspaceKey", TenantKind.Workspace)]
	static async Task<IResult> Create(HttpContext context, IConfigDirectory config, string workspaceKey, ConfigBindingDto dto)
	{
		if (string.IsNullOrWhiteSpace(dto.Path))
			return Results.BadRequest(new ErrorResponse("path is required"));
		if (!dto.Tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
			return Results.BadRequest(new ErrorResponse($"Tags must include 'ws:{workspaceKey}'"));

		var value = dto.Value ?? string.Empty;
		var binding = await config.CreateBindingAsync(workspaceKey, dto.Path, value, dto.Tags, dto.Kind, context.RequestAborted);
		return Results.Ok(new ConfigBindingCreatedResponse(binding.Id, binding.Path, binding.Tags));
	}

	// Soft-delete: mark IsDeleted=1, keep the row. Resolve filters it out.
	// UI's history page can offer "Undelete" for the last deleted version.
	[TenantFrom(TenantSource.Route, "workspaceKey", TenantKind.Workspace)]
	static async Task<IResult> Delete(HttpContext context, IConfigDirectory config, string workspaceKey, string path, string tags)
	{
		var deleted = await config.DeleteBindingByPathTagsAsync(workspaceKey, path, tags, context.RequestAborted);

		return deleted
			? Results.Ok(new DeletedResponse(true))
			: Results.NotFound(new ErrorResponse("binding not found"));
	}

}
