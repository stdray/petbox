using System.ComponentModel;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
		IHttpContextAccessor http, ICoreDbFactory dbf,
		[Description("Human-readable key name.")] string name,
		[Description("Comma-separated scope list, e.g. 'data:read,data:write'.")] string scopes,
		[Description("Project the key is scoped to. Required unless allProjects.")] string? projectKey = null,
		[Description("Optional TTL in seconds; omit for a non-expiring key.")] long? expiresInSeconds = null,
		[Description("Mint a cross-project key (claim '*'). Omit projectKey when set.")] bool allProjects = false,
		[Description("Fallback project when a tool's optional projectKey is omitted. Only with allProjects:true.")] string? defaultProject = null,
		[Description("Mint a SANDBOX-ONLY key: writes are additionally gated to projects flagged sandbox (see project_create). Default false.")] bool sandboxOnly = false,
		CancellationToken ct = default)
	{
		using var db = dbf.Open();
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
	[Description("Lists a project's API keys (key, name, scopes, created/expiry, defaultProjectKey, lastUsedAt). Requires admin:provision. Pass projectKey '*' to list the cross-project keys — `defaultProjectKey` is the project such a key falls back to when a tool's optional projectKey is omitted. `lastUsedAt` is the last successful authentication with the key, null if it has never been used: it is served FRESH (the stored value merged with the in-memory stamp), so a call made seconds ago already shows — but the STORED value is coarse (persisted in ~5-minute batches), and a hard crash can lose up to that window.")]
	public static async Task<ApiKeyListResult> ListAsync(
		IHttpContextAccessor http, ICoreDbFactory dbf, IKeyStatService stats,
		[Description("Project to list keys for.")] string projectKey,
		CancellationToken ct = default)
	{
		using var db = dbf.Open();
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(projectKey)) throw new ArgumentException("projectKey is required");
		var rows = await db.ApiKeys
			.Where(k => k.ProjectKey == projectKey)
			.OrderBy(k => k.CreatedAt)
			.Select(k => new ApiKeyRow(k.Key, k.Name, k.Scopes, k.CreatedAt, k.ExpiresAt, k.DefaultProjectKey, k.SandboxOnly, k.LastUsedAt))
			.ToListAsync(ct);
		// The merge that keeps the answer honest: the flusher runs every ~5 minutes, so the column
		// alone would show a key as idle minutes after it was actually used. Take the LATER of the
		// stored value and the live in-memory stamp (spec apikey-last-used).
		return new ApiKeyListResult([.. rows.Select(r => r with { LastUsedAt = Later(r.LastUsedAt, stats.LastUsed(r.Key)) })]);
	}

	static DateTime? Later(DateTime? stored, DateTime? inMemory) =>
		(stored, inMemory) switch
		{
			(null, var m) => m,
			(var s, null) => s,
			var (s, m) => s >= m ? s : m,
		};

	[McpServerTool(Name = "apikey_update", Title = "Update an API key", UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyUpdatedResult))]
	[Description("PATCHes an ALREADY-ISSUED key in place — no re-mint, no manual DB edit. Requires admin:provision: exactly the right apikey_create needs, so an update can never grant what a mint could not. The secret itself never changes and is never returned; `key` is the address. Editable: `name`, `scopes`, expiry, `defaultProject`. A field you OMIT is left untouched (it is NOT reset to a default). The two clearable fields have an explicit sentinel, distinct from 'omitted': `expiresInSeconds:0` makes the key NON-EXPIRING, `defaultProject:\"\"` (empty string) DROPS the default project. `scopes` replaces the whole set (it is not additive) and is validated like on create — unknown scopes are rejected, an empty set is rejected. `defaultProject` obeys the same invariants as create: cross-project ('*') keys only, and the project must exist. A change takes effect on the NEXT call with that key — nothing about a key is cached per connection or per session. A key declared in appsettings (Auth:ApiKeys) CANNOT be updated: the config file owns its lifecycle and the config lookup wins on every auth, so a stored row would never be read — such a call is REFUSED, not silently ignored.")]
	public static async Task<ApiKeyUpdatedResult> UpdateAsync(
		IHttpContextAccessor http, ICoreDbFactory dbf,
		[Description("The raw key value to update (the secret is not changed).")] string key,
		[Description("New human-readable name. Omit to leave unchanged.")] string? name = null,
		[Description("New comma-separated scope list — REPLACES the current set. Omit to leave unchanged.")] string? scopes = null,
		[Description("New TTL in seconds from now. 0 = clear the expiry (never expires). Omit to leave unchanged.")] long? expiresInSeconds = null,
		[Description("New fallback project (cross-project keys only). \"\" = drop it. Omit to leave unchanged.")] string? defaultProject = null,
		CancellationToken ct = default)
	{
		using var db = dbf.Open();
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required");

		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");

		// A config-declared key is refused BEFORE anything is written. CompositeApiKeyLookup asks
		// config FIRST and the DB only on a miss, so a stored row for a config key would never be
		// read back — the update would look like it worked and change nothing. That is exactly the
		// silent no-op spec apikey-update-config-key-refused forbids.
		var configKeys = ctx.RequestServices.GetRequiredService<ConfigApiKeyLookup>();
		if (configKeys.FindByKey(key) is not null)
			throw new InvalidOperationException(
				"This key is declared in configuration (Auth:ApiKeys) — its lifecycle belongs to the config file, "
				+ "not the database. A stored change would never take effect (config wins on every auth lookup), "
				+ "so it is refused: edit appsettings (or mint a DB key with apikey_create) instead.");

		var existing = await db.ApiKeys.FirstOrDefaultAsync((ApiKey k) => k.Key == key, ct)
			?? throw new InvalidOperationException("ApiKey not found");

		// Patch onto the LOADED row, so every field the caller did not name keeps its stored value
		// bit-for-bit — the UPDATE below writes all mapped columns, and they carry `existing`'s data.
		var updated = existing;
		var touched = new List<string>();

		if (name is not null)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("name cannot be blank (omit it to leave the name unchanged)");
			updated = updated with { Name = name.Trim() };
			touched.Add("name");
		}

		if (scopes is not null)
		{
			var (valid, invalid) = ApiKeyScopes.Validate(scopes);
			if (invalid.Count > 0) throw new ArgumentException($"Unknown scopes: {string.Join(", ", invalid)}");
			if (valid.Count == 0) throw new ArgumentException("At least one valid scope is required");
			updated = updated with { Scopes = string.Join(',', valid) };
			touched.Add("scopes");
		}

		if (expiresInSeconds is { } secs)
		{
			if (secs < 0)
				throw new ArgumentException("expiresInSeconds must be >= 0 (0 clears the expiry — the key stops expiring)");
			updated = updated with { ExpiresAt = secs == 0 ? null : DateTime.UtcNow.AddSeconds(secs) };
			touched.Add("expiry");
		}

		if (defaultProject is not null)
		{
			var dflt = defaultProject.Trim();
			if (dflt.Length == 0)
			{
				// The explicit clear — distinct from omitting the argument, which changes nothing.
				updated = updated with { DefaultProjectKey = null };
			}
			else
			{
				// The create-time invariants, reused verbatim: a default project is meaningful only on a
				// cross-project key (a project-scoped one already defaults to its own claim), and it must
				// name a project that exists.
				if (existing.ProjectKey != ProjectScope.AllProjects)
					throw new ArgumentException("defaultProject is only valid on a cross-project ('*') key (a project-scoped key already defaults to its own project)");
				if (!await db.Projects.AnyAsync((Project p) => p.Key == dflt, ct))
					throw new InvalidOperationException($"Project '{dflt}' not found");
				updated = updated with { DefaultProjectKey = dflt };
			}
			touched.Add("defaultProject");
		}

		if (touched.Count == 0)
			throw new ArgumentException("Nothing to update — pass at least one of name / scopes / expiresInSeconds / defaultProject");

		await db.UpdateAsync(updated, token: ct);

		// spec access-attribution: the change is attributable to the key that made it. Neither key is
		// logged in full — a suffix identifies the row without putting a live secret in the log.
		var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PetBox.Web.Mcp.ApiKeyTools");
		if (logger.IsEnabled(LogLevel.Information))
			logger.LogInformation("apikey_update target={TargetKey} fields={Fields} actor={ActorKey} actorProject={ActorProject}",
				Tail(key), string.Join(',', touched), Tail(ctx.Request.Headers[ApiKeyAuthenticationHandler.ApiKeyHeader].FirstOrDefault()),
				ctx.User.FindFirst("project")?.Value);

		return new ApiKeyUpdatedResult(
			updated.Key, updated.ProjectKey,
			ApiKeyScopes.Validate(updated.Scopes).Valid,
			updated.ExpiresAt, updated.DefaultProjectKey, updated.SandboxOnly, touched);
	}

	// Last 6 chars of a key — enough to identify the row in a log line, never the secret itself.
	static string Tail(string? key) =>
		string.IsNullOrEmpty(key) ? "(none)" : key.Length <= 6 ? "…" : $"…{key[^6..]}";

	[McpServerTool(Name = "apikey_delete", Title = "Delete an API key", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ApiKeyDeletedResult))]
	[Description("Deletes (revokes) an API key by its raw key value. Requires admin:provision.")]
	public static async Task<ApiKeyDeletedResult> DeleteAsync(
		IHttpContextAccessor http, ICoreDbFactory dbf,
		[Description("The raw key value to revoke.")] string key,
		CancellationToken ct = default)
	{
		using var db = dbf.Open();
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required");
		var deleted = await db.ApiKeys.Where(k => k.Key == key).DeleteAsync(ct);
		if (deleted == 0) throw new InvalidOperationException("ApiKey not found");
		return new ApiKeyDeletedResult(true, key);
	}
}
