using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Services;

namespace PetBox.Web.AgentDefs;

// REST surface for portable agent-definition documents (agent-definition-as-data).
// Project-scoped named temporal docs; CAS via version watermark (0 = create).
//   GET    /api/{projectKey}/agent-defs
//   GET    /api/{projectKey}/agent-defs/{key}
//   PUT    /api/{projectKey}/agent-defs/{key}   body: { version, definition }
//   DELETE /api/{projectKey}/agent-defs/{key}?version=
// Auth: RequireAuthorization("ApiKey") then agents:read / agents:write + project claim.
// Always on (Core DB) — no feature flag.
public static class AgentDefsApi
{
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	public static void MapAgentDefsEndpoints(this IEndpointRouteBuilder app)
	{
		app.MapGet("/api/{projectKey}/agent-defs", ListAsync)
			.Produces<AgentDefinitionListResponse>()
			.RequireAuthorization("ApiKey");

		app.MapGet("/api/{projectKey}/agent-defs/{key}", GetAsync)
			.Produces<AgentDefinitionView>()
			.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
			.RequireAuthorization("ApiKey");

		app.MapPut("/api/{projectKey}/agent-defs/{key}", PutAsync)
			.Accepts<AgentDefinitionPutBody>("application/json")
			.Produces<AgentDefinitionAck>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status409Conflict)
			.RequireAuthorization("ApiKey");

		app.MapDelete("/api/{projectKey}/agent-defs/{key}", DeleteAsync)
			.Produces<AgentDefinitionAck>()
			.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
			.Produces<ErrorResponse>(StatusCodes.Status409Conflict)
			.RequireAuthorization("ApiKey");
	}

	// PUT body: version watermark + the portable definition document.
	public sealed record AgentDefinitionPutBody(long Version, JsonElement Definition);

	public sealed record AgentDefinitionListResponse(IReadOnlyList<AgentDefinitionListItem> Items);

	static async Task<IResult> ListAsync(
		HttpContext ctx, string projectKey, IAgentDefinitionService svc, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeAsync(ctx, projectKey, ApiKeyScopes.AgentsRead, catalog, ct) is { } forbid)
			return forbid;
		var items = await svc.ListAsync(projectKey, ct);
		return TypedResults.Ok(new AgentDefinitionListResponse(items));
	}

	static async Task<IResult> GetAsync(
		HttpContext ctx, string projectKey, string key, IAgentDefinitionService svc, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeAsync(ctx, projectKey, ApiKeyScopes.AgentsRead, catalog, ct) is { } forbid)
			return forbid;
		try
		{
			var view = await svc.GetAsync(projectKey, key, ct);
			return view is null
				? TypedResults.NotFound(new ErrorResponse($"agent definition '{key}' not found"))
				: TypedResults.Ok(view);
		}
		catch (ArgumentException ex)
		{
			return TypedResults.BadRequest(new ErrorResponse(ex.Message));
		}
	}

	static async Task<IResult> PutAsync(
		HttpContext ctx, string projectKey, string key, IAgentDefinitionService svc, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeAsync(ctx, projectKey, ApiKeyScopes.AgentsWrite, catalog, ct) is { } forbid)
			return forbid;

		AgentDefinitionPutBody? body;
		try
		{
			body = await JsonSerializer.DeserializeAsync<AgentDefinitionPutBody>(ctx.Request.Body, Json, ct);
		}
		catch (JsonException ex)
		{
			return TypedResults.BadRequest(new ErrorResponse($"invalid JSON: {ex.Message}"));
		}
		if (body is null || body.Definition.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
			return TypedResults.BadRequest(new ErrorResponse("body.definition is required"));

		try
		{
			// Parse from JsonElement so role.model is rejected on the wire shape.
			var def = AgentDefinitionJson.Parse(body.Definition);
			var ack = await svc.UpsertAsync(projectKey, key, def, body.Version, ct);
			return TypedResults.Ok(ack);
		}
		catch (ArgumentException ex)
		{
			return TypedResults.BadRequest(new ErrorResponse(ex.Message));
		}
		catch (InvalidOperationException ex) when (IsConflict(ex.Message))
		{
			return TypedResults.Conflict(new ErrorResponse(ex.Message));
		}
	}

	static async Task<IResult> DeleteAsync(
		HttpContext ctx, string projectKey, string key, IAgentDefinitionService svc, IProjectCatalog catalog, CancellationToken ct)
	{
		if (await AuthorizeAsync(ctx, projectKey, ApiKeyScopes.AgentsWrite, catalog, ct) is { } forbid)
			return forbid;

		long version = 0;
		var raw = ctx.Request.Query["version"].FirstOrDefault();
		if (raw is not null && (!long.TryParse(raw, out version) || version < 0))
			return TypedResults.BadRequest(new ErrorResponse("version must be a non-negative integer"));

		try
		{
			var ack = await svc.DeleteAsync(projectKey, key, version, ct);
			return TypedResults.Ok(ack);
		}
		catch (ArgumentException ex)
		{
			return TypedResults.BadRequest(new ErrorResponse(ex.Message));
		}
		catch (InvalidOperationException ex) when (IsConflict(ex.Message))
		{
			return TypedResults.Conflict(new ErrorResponse(ex.Message));
		}
	}

	static async Task<IResult?> AuthorizeAsync(
		HttpContext ctx, string projectKey, string scope, IProjectCatalog catalog, CancellationToken ct)
	{
		if (!await ProjectScope.AuthorizesAsync(ctx.User, projectKey, catalog, ct))
			return Results.Forbid();
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(scope, StringComparer.Ordinal))
			return Results.Forbid();
		return null;
	}

	static bool IsConflict(string message) =>
		message.Contains("conflict", StringComparison.OrdinalIgnoreCase);
}
