using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Auth;
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
		IHttpContextAccessor http, AgentKeyAdminService keys,
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
			effectiveDefault = string.IsNullOrWhiteSpace(defaultProject) ? null : defaultProject.Trim();
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
			effectiveProject = projectKey;
		}

		var (valid, invalid) = ApiKeyScopes.Validate(scopes);
		if (invalid.Count > 0) throw new ArgumentException($"Unknown scopes: {string.Join(", ", invalid)}");
		if (valid.Count == 0) throw new ArgumentException("At least one valid scope is required");

		DateTime? expiresAt = expiresInSeconds is { } secs and > 0 ? DateTime.UtcNow.AddSeconds(secs) : null;

		// Everything the DATABASE decides — the project exists, a sandboxOnly key names a sandbox
		// project, the default project exists — lives in AgentKeyAdminService, the one door onto ApiKeys.
		var minted = await keys.MintAsync(
			new AgentKeyMint(name, valid, effectiveProject, expiresAt, effectiveDefault, sandboxOnly), ct);

		return minted switch
		{
			KeyMintResult.Minted m => new ApiKeyCreatedResult(
				m.Key.Key, m.Key.ProjectKey, valid, m.Key.ExpiresAt, m.Key.DefaultProjectKey, m.Key.SandboxOnly),
			KeyMintResult.NotFound nf => throw new InvalidOperationException(nf.Reason),
			KeyMintResult.Refused r => throw new ArgumentException(r.Reason),
			_ => throw new InvalidOperationException("ApiKey could not be minted"),
		};
	}

	[McpServerTool(Name = "apikey_list", Title = "List API keys", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyListResult))]
	[Description("Lists a project's API keys (key, name, scopes, created/expiry, defaultProjectKey, lastUsedAt). Requires admin:provision. Pass projectKey '*' to list the cross-project keys — `defaultProjectKey` is the project such a key falls back to when a tool's optional projectKey is omitted. `lastUsedAt` is the last successful authentication with the key, null if it has never been used: it is served FRESH (the stored value merged with the in-memory stamp), so a call made seconds ago already shows — but the STORED value is coarse (persisted in ~5-minute batches), and a hard crash can lose up to that window.")]
	public static async Task<ApiKeyListResult> ListAsync(
		IHttpContextAccessor http, AgentKeyAdminService keys,
		[Description("Project to list keys for.")] string projectKey,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(projectKey)) throw new ArgumentException("projectKey is required");

		// The service merges the stored LastUsedAt with the live in-memory stamp (the flusher persists
		// only every ~5 minutes, so the column alone would report a key used seconds ago as idle).
		var rows = await keys.ListByProjectAsync(projectKey, ct);
		return new ApiKeyListResult(
			[.. rows.Select(k => new ApiKeyRow(
				k.Key, k.Name, k.Scopes, k.CreatedAt, k.ExpiresAt, k.DefaultProjectKey, k.SandboxOnly, k.LastUsedAt))]);
	}

	[McpServerTool(Name = "apikey_update", Title = "Update an API key", UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyUpdatedResult))]
	[Description("PATCHes an ALREADY-ISSUED key in place — no re-mint, no manual DB edit. Requires admin:provision: exactly the right apikey_create needs, so an update can never grant what a mint could not. The secret itself never changes and is never returned; `key` is the address. Editable: `name`, `scopes`, expiry, `defaultProject`. A field you OMIT is left untouched (it is NOT reset to a default). The two clearable fields have an explicit sentinel, distinct from 'omitted': `expiresInSeconds:0` makes the key NON-EXPIRING, `defaultProject:\"\"` (empty string) DROPS the default project. `scopes` replaces the whole set (it is not additive) and is validated like on create — unknown scopes are rejected, an empty set is rejected. `defaultProject` obeys the same invariants as create: cross-project ('*') keys only, and the project must exist. A change takes effect on the NEXT call with that key — nothing about a key is cached per connection or per session. A key declared in appsettings (Auth:ApiKeys) CANNOT be updated: the config file owns its lifecycle and the config lookup wins on every auth, so a stored row would never be read — such a call is REFUSED, not silently ignored.")]
	public static async Task<ApiKeyUpdatedResult> UpdateAsync(
		IHttpContextAccessor http, AgentKeyAdminService keys,
		[Description("The raw key value to update (the secret is not changed).")] string key,
		[Description("New human-readable name. Omit to leave unchanged.")] string? name = null,
		[Description("New comma-separated scope list — REPLACES the current set. Omit to leave unchanged.")] string? scopes = null,
		[Description("New TTL in seconds from now. 0 = clear the expiry (never expires). Omit to leave unchanged.")] long? expiresInSeconds = null,
		[Description("New fallback project (cross-project keys only). \"\" = drop it. Omit to leave unchanged.")] string? defaultProject = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required");

		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");

		// The patch rules — a null field is left untouched, the two sentinels clear, a config-declared
		// key is refused rather than silently no-op'd, scopes are validated exactly as on a mint — live
		// in AgentKeyAdminService, alongside the admin pages' edit. One door onto ApiKeys.
		var result = await keys.PatchAsync(
			new AgentKeyPatch(key, name, scopes, expiresInSeconds, defaultProject), ct);

		var patched = result switch
		{
			KeyPatchResult.Patched p => p,
			KeyPatchResult.NotFound => throw new InvalidOperationException("ApiKey not found"),
			// A named thing that is not there, and a key whose lifecycle belongs to the config file: both
			// are state refusals (InvalidOperationException). A Refused is a bad argument.
			KeyPatchResult.Missing m => throw new InvalidOperationException(m.Reason),
			KeyPatchResult.Immutable i => throw new InvalidOperationException(i.Reason),
			KeyPatchResult.Refused r => throw new ArgumentException(r.Reason),
			_ => throw new InvalidOperationException("ApiKey could not be updated"),
		};
		var updated = patched.Key;

		// spec access-attribution: the change is attributable to the key that made it. Neither key is
		// logged in full — a suffix identifies the row without putting a live secret in the log.
		var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PetBox.Web.Mcp.ApiKeyTools");
		if (logger.IsEnabled(LogLevel.Information))
			logger.LogInformation("apikey_update target={TargetKey} fields={Fields} actor={ActorKey} actorProject={ActorProject}",
				Tail(key), string.Join(',', patched.Touched), Tail(ctx.Request.Headers[ApiKeyAuthenticationHandler.ApiKeyHeader].FirstOrDefault()),
				ctx.User.FindFirst("project")?.Value);

		return new ApiKeyUpdatedResult(
			updated.Key, updated.ProjectKey,
			ApiKeyScopes.Validate(updated.Scopes).Valid,
			updated.ExpiresAt, updated.DefaultProjectKey, updated.SandboxOnly, patched.Touched);
	}

	// Last 6 chars of a key — enough to identify the row in a log line, never the secret itself.
	static string Tail(string? key) =>
		string.IsNullOrEmpty(key) ? "(none)" : key.Length <= 6 ? "…" : $"…{key[^6..]}";

	[McpServerTool(Name = "apikey_delete", Title = "Delete an API key", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyDeletedResult))]
	[Description("Deletes (revokes) an API key by its raw key value. Requires admin:provision.")]
	public static async Task<ApiKeyDeletedResult> DeleteAsync(
		IHttpContextAccessor http, AgentKeyAdminService keys,
		[Description("The raw key value to revoke.")] string key,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required");
		if (!await keys.DeleteAsync(key, ct)) throw new InvalidOperationException("ApiKey not found");
		return new ApiKeyDeletedResult(true, key);
	}
}
