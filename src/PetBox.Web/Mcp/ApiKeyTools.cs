using System.ComponentModel;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Typed per-type API-key provisioning tools (typed-surface Phase 4 — replaces the generic
// entity.* type "apikey"). Flat, typed params → a real per-field schema. Provisioning ops:
// admin:provision scope, NO per-project claim (cross-project onboarding). create returns the
// raw key ONCE (never retrievable again). Tools throw on a failed Assert*/validation;
// McpErrorEnvelopeFilter renders the exception as the structured {error} body.
[McpServerToolType]
public static class ApiKeyTools
{
	[McpServerTool(Name = "apikey.create", Title = "Mint an API key", UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyCreatedResult))]
	[Description("Mints a project-scoped API key. Requires admin:provision. `scopes` is a comma-separated list; unknown scopes are rejected. `expiresInSeconds` (optional) sets a TTL. The raw key is returned ONCE — store it now.")]
	public static async Task<ApiKeyCreatedResult> CreateAsync(
		IHttpContextAccessor http, PetBoxDb db,
		[Description("Project the key is scoped to.")] string projectKey,
		[Description("Human-readable key name.")] string name,
		[Description("Comma-separated scope list, e.g. 'data:read,data:write'.")] string scopes,
		[Description("Optional TTL in seconds; omit for a non-expiring key.")] long? expiresInSeconds = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(projectKey)) throw new ArgumentException("projectKey is required");
		if (string.IsNullOrWhiteSpace(scopes)) throw new ArgumentException("scopes is required");

		if (!await db.Projects.AnyAsync((Project p) => p.Key == projectKey, ct))
			throw new InvalidOperationException($"Project '{projectKey}' not found");

		var (valid, invalid) = ApiKeyScopes.Validate(scopes);
		if (invalid.Count > 0) throw new ArgumentException($"Unknown scopes: {string.Join(", ", invalid)}");
		if (valid.Count == 0) throw new ArgumentException("At least one valid scope is required");

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		DateTime? expiresAt = expiresInSeconds is { } secs and > 0 ? DateTime.UtcNow.AddSeconds(secs) : null;

		await db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = projectKey,
			Scopes = string.Join(',', valid),
			Name = string.IsNullOrWhiteSpace(name) ? "agent-minted" : name.Trim(),
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = expiresAt,
		}, token: ct);
		return new ApiKeyCreatedResult(keyValue, projectKey, valid, expiresAt);
	}

	[McpServerTool(Name = "apikey.list", Title = "List API keys", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyListResult))]
	[Description("Lists a project's API keys (key, name, scopes, created/expiry). Requires admin:provision.")]
	public static async Task<ApiKeyListResult> ListAsync(
		IHttpContextAccessor http, PetBoxDb db,
		[Description("Project to list keys for.")] string projectKey,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(projectKey)) throw new ArgumentException("projectKey is required");
		var rows = await db.ApiKeys
			.Where(k => k.ProjectKey == projectKey)
			.OrderBy(k => k.CreatedAt)
			.Select(k => new ApiKeyRow(k.Key, k.Name, k.Scopes, k.CreatedAt, k.ExpiresAt))
			.ToListAsync(ct);
		return new ApiKeyListResult(rows);
	}

	[McpServerTool(Name = "apikey.delete", Title = "Delete an API key", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyDeletedResult))]
	[Description("Deletes (revokes) an API key by its raw key value. Requires admin:provision.")]
	public static async Task<ApiKeyDeletedResult> DeleteAsync(
		IHttpContextAccessor http, PetBoxDb db,
		[Description("The raw key value to revoke.")] string key,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required");
		var deleted = await db.ApiKeys.Where(k => k.Key == key).DeleteAsync(ct);
		if (deleted == 0) throw new InvalidOperationException("ApiKey not found");
		return new ApiKeyDeletedResult(true, key);
	}
}
