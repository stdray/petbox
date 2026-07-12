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
	[McpServerTool(Name = "apikey_create", Title = "Mint an API key", UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyCreatedResult))]
	[Description("Mints a project-scoped API key. Requires admin:provision. `scopes` is a comma-separated list; unknown scopes are rejected. `expiresInSeconds` (optional) sets a TTL. `allProjects:true` mints a CROSS-PROJECT key (project claim '*', reads+writes every project) — `projectKey` must be omitted then. `defaultProject` (cross-project keys ONLY) is the project the tools with an OPTIONAL projectKey fall back to when it is omitted; a project-scoped key already defaults to its own claim, so passing it there is an error. `sandboxOnly:true` mints a SANDBOX key (spec work/smoke-writes-into-real-projects): on top of the normal claim check, every write is also required to land in a project flagged `sandbox` (project_create sandbox:true) — this holds even for a `sandboxOnly:true, allProjects:true` key (one smoke key spanning every sandbox project, still refused on every real one). With a specific `projectKey` (not allProjects), that project must already be a sandbox project. The raw key is returned ONCE — store it now.")]
	public static async Task<ApiKeyCreatedResult> CreateAsync(
		IHttpContextAccessor http, PetBoxDb db,
		[Description("Human-readable key name.")] string name,
		[Description("Comma-separated scope list, e.g. 'data:read,data:write'.")] string scopes,
		[Description("Project the key is scoped to. Required unless allProjects.")] string? projectKey = null,
		[Description("Optional TTL in seconds; omit for a non-expiring key.")] long? expiresInSeconds = null,
		[Description("Mint a cross-project key (claim '*'). Omit projectKey when set.")] bool allProjects = false,
		[Description("Fallback project when a tool's optional projectKey is omitted. Only with allProjects:true.")] string? defaultProject = null,
		[Description("Mint a SANDBOX-ONLY key: writes are additionally gated to projects flagged sandbox (see project_create). Default false.")] bool sandboxOnly = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		// A key's name is its only human-readable label in the admin list; blank names are
		// the prod-data symptom we're closing off. Reject empty/whitespace here (the admin UI
		// already does) — duplicates are still allowed (names are labels, not identifiers).
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
		if (string.IsNullOrWhiteSpace(scopes)) throw new ArgumentException("scopes is required");

		string effectiveProject;
		string? effectiveDefault = null;
		if (allProjects)
		{
			// The wildcard lives only in the claim (ProjectScope.AllProjects) — it is not a
			// project row, so the existence check is skipped. An explicit projectKey alongside
			// allProjects is ambiguous; reject rather than guess.
			if (!string.IsNullOrWhiteSpace(projectKey) && projectKey != ProjectScope.AllProjects)
				throw new ArgumentException("allProjects and projectKey are mutually exclusive — omit projectKey");
			effectiveProject = ProjectScope.AllProjects;
			if (!string.IsNullOrWhiteSpace(defaultProject))
			{
				var dflt = defaultProject.Trim();
				if (!await db.Projects.AnyAsync((Project p) => p.Key == dflt, ct))
					throw new InvalidOperationException($"Project '{dflt}' not found");
				effectiveDefault = dflt;
			}
			// sandboxOnly + allProjects is deliberately VALID (one smoke key spanning every sandbox
			// project) — the per-project containment check then runs per-CALL, not at mint time.
		}
		else
		{
			// A project-scoped key ALREADY defaults to its own claim (ModuleMcp.ResolveProject),
			// so a second, possibly divergent default would be a silent contradiction — reject.
			if (!string.IsNullOrWhiteSpace(defaultProject))
				throw new ArgumentException("defaultProject is only valid with allProjects:true (a project-scoped key already defaults to its own project)");
			if (string.IsNullOrWhiteSpace(projectKey)) throw new ArgumentException("projectKey is required (or pass allProjects:true)");
			var project = await db.Projects.FirstOrDefaultAsync((Project p) => p.Key == projectKey, ct);
			if (project is null)
				throw new InvalidOperationException($"Project '{projectKey}' not found");
			// A sandboxOnly key scoped to a SPECIFIC, non-sandbox project could never write anything
			// (ProjectScope.AuthorizesAsync would refuse every call) — reject the mint rather than
			// hand out a key that is silently useless (spec work/smoke-writes-into-real-projects).
			if (sandboxOnly && !project.Sandbox)
				throw new ArgumentException(
					$"sandboxOnly:true requires projectKey '{projectKey}' to be a sandbox project (project_create sandbox:true) — "
					+ "a sandbox-only key scoped to a non-sandbox project could never write anything");
			effectiveProject = projectKey;
		}

		var (valid, invalid) = ApiKeyScopes.Validate(scopes);
		if (invalid.Count > 0) throw new ArgumentException($"Unknown scopes: {string.Join(", ", invalid)}");
		if (valid.Count == 0) throw new ArgumentException("At least one valid scope is required");

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		DateTime? expiresAt = expiresInSeconds is { } secs and > 0 ? DateTime.UtcNow.AddSeconds(secs) : null;

		await db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = effectiveProject,
			Scopes = string.Join(',', valid),
			Name = name.Trim(),
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = expiresAt,
			DefaultProjectKey = effectiveDefault,
			SandboxOnly = sandboxOnly,
		}, token: ct);
		return new ApiKeyCreatedResult(keyValue, effectiveProject, valid, expiresAt, effectiveDefault, sandboxOnly);
	}

	[McpServerTool(Name = "apikey_list", Title = "List API keys", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyListResult))]
	[Description("Lists a project's API keys (key, name, scopes, created/expiry, defaultProjectKey). Requires admin:provision. Pass projectKey '*' to list the cross-project keys — `defaultProjectKey` is the project such a key falls back to when a tool's optional projectKey is omitted.")]
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
			.Select(k => new ApiKeyRow(k.Key, k.Name, k.Scopes, k.CreatedAt, k.ExpiresAt, k.DefaultProjectKey, k.SandboxOnly))
			.ToListAsync(ct);
		return new ApiKeyListResult(rows);
	}

	[McpServerTool(Name = "apikey_delete", Title = "Delete an API key", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyDeletedResult))]
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
