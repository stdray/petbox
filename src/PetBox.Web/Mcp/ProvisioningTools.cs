using System.ComponentModel;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Mcp;

// Agent-onboarding provisioning tools (Phase 27.2). Unlike the Data/Log tools these are
// cross-project — they CREATE projects, services, keys, and config bindings — so they
// authorize on the `admin:provision` scope only (no per-project claim cross-check). Issue
// `admin:provision` only on short-lived agent keys (ApiKey.ExpiresAt) minted from the
// sysadmin Agent Keys page.
[McpServerToolType]
public static class ProvisioningTools
{
	// Project/service keys share the DataDb name spec: lowercase identifier, a-z0-9_- , ≤100 chars.
	static readonly Regex KeyRegex = new("^[a-z][a-z0-9_-]{0,99}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	[McpServerTool(Name = "workspace.create_project", Title = "Create project")]
	[Description("Creates a project inside a workspace. Requires admin:provision scope.")]
	public static async Task<object> CreateProjectAsync(
		IHttpContextAccessor http,
		PetBoxDb db,
		[Description("Workspace key the project belongs to.")] string workspaceKey,
		[Description("Project key. Lowercase identifier, unique across the instance.")] string key,
		[Description("Human-readable project name.")] string name,
		[Description("Optional description.")] string? description = null,
		CancellationToken ct = default)
	{
		AssertScope(http, "admin:provision");
		Require(workspaceKey, nameof(workspaceKey));
		RequireKey(key, nameof(key));

		if (!await db.Workspaces.AnyAsync((Workspace w) => w.Key == workspaceKey, ct))
			throw new InvalidOperationException($"Workspace '{workspaceKey}' not found");
		if (await db.Projects.AnyAsync((Project p) => p.Key == key, ct))
			throw new InvalidOperationException($"Project '{key}' already exists");

		await db.InsertAsync(new Project
		{
			Key = key,
			WorkspaceKey = workspaceKey,
			Name = string.IsNullOrWhiteSpace(name) ? key : name,
			Description = description ?? string.Empty,
		}, token: ct);

		return new { key, workspaceKey, name, description };
	}

	[McpServerTool(Name = "project.create_service", Title = "Create service")]
	[Description("Registers a service under a project. Requires admin:provision scope.")]
	public static async Task<object> CreateServiceAsync(
		IHttpContextAccessor http,
		PetBoxDb db,
		[Description("Project key the service belongs to.")] string projectKey,
		[Description("Service key, unique across the instance.")] string key,
		[Description("Health model: 'endpoint' (petbox polls a URL) or 'push' (service reports in).")] string healthModel = "push",
		[Description("Optional URL for endpoint health checks.")] string? url = null,
		CancellationToken ct = default)
	{
		AssertScope(http, "admin:provision");
		Require(projectKey, nameof(projectKey));
		RequireKey(key, nameof(key));

		if (!await db.Projects.AnyAsync((Project p) => p.Key == projectKey, ct))
			throw new InvalidOperationException($"Project '{projectKey}' not found");
		if (await db.Services.AnyAsync((Service s) => s.Key == key, ct))
			throw new InvalidOperationException($"Service '{key}' already exists");

		var model = string.Equals(healthModel, "endpoint", StringComparison.OrdinalIgnoreCase)
			? HealthModel.Endpoint
			: HealthModel.Push;

		await db.InsertAsync(new Service
		{
			Key = key,
			ProjectKey = projectKey,
			HealthModel = model,
			Url = url,
			Health = ServiceHealth.Unknown,
		}, token: ct);

		return new { key, projectKey, healthModel = model.ToString(), url };
	}

	[McpServerTool(Name = "project.create_apikey", Title = "Mint API key")]
	[Description("Mints a project-scoped API key and returns the raw key value ONCE. Optionally time-limited via expiresInSeconds. Requires admin:provision scope.")]
	public static async Task<object> CreateApiKeyAsync(
		IHttpContextAccessor http,
		PetBoxDb db,
		[Description("Project key the new key is scoped to.")] string projectKey,
		[Description("Human-readable label for the key.")] string name,
		[Description("Comma-separated scopes, e.g. 'config:read,data:read,data:write'. Validated against the scope catalog.")] string scopes,
		[Description("Optional TTL in seconds. Omit for a non-expiring key.")] long? expiresInSeconds = null,
		CancellationToken ct = default)
	{
		AssertScope(http, "admin:provision");
		Require(projectKey, nameof(projectKey));

		if (!await db.Projects.AnyAsync((Project p) => p.Key == projectKey, ct))
			throw new InvalidOperationException($"Project '{projectKey}' not found");

		var (valid, invalid) = ApiKeyScopes.Validate(scopes);
		if (invalid.Count > 0)
			throw new ArgumentException($"Unknown scopes: {string.Join(", ", invalid)}");
		if (valid.Count == 0)
			throw new ArgumentException("At least one valid scope is required");

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		DateTime? expiresAt = expiresInSeconds is { } secs and > 0
			? DateTime.UtcNow.AddSeconds(secs)
			: null;

		await db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = projectKey,
			Scopes = string.Join(',', valid),
			Name = string.IsNullOrWhiteSpace(name) ? "agent-minted" : name.Trim(),
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = expiresAt,
		}, token: ct);

		return new { key = keyValue, projectKey, scopes = valid, expiresAt };
	}

	[McpServerTool(Name = "project.set_config_binding", Title = "Set config binding")]
	[Description("Creates a config binding in a workspace. Requires admin:provision scope.")]
	public static async Task<object> SetConfigBindingAsync(
		IHttpContextAccessor http,
		IConfigDbFactory configFactory,
		[Description("Workspace key whose config store holds the binding.")] string workspaceKey,
		[Description("Config path, e.g. 'db.host'.")] string path,
		[Description("Value to store.")] string value,
		[Description("Comma-separated tags. Must include 'ws:{workspaceKey}'.")] string tags,
		CancellationToken ct = default)
	{
		AssertScope(http, "admin:provision");
		Require(workspaceKey, nameof(workspaceKey));
		Require(path, nameof(path));

		if (!tags.Contains($"ws:{workspaceKey}", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"Tags must include 'ws:{workspaceKey}'");

		var now = DateTime.UtcNow;
		var v = value ?? string.Empty;
		var configDb = configFactory.GetConfigDb(workspaceKey);
#pragma warning disable CA2016
		var id = Convert.ToInt64(await configDb.InsertWithIdentityAsync(new ConfigBinding
		{
			Path = path,
			Value = v,
			Tags = tags,
			Kind = BindingKind.Plain,
			Version = 1,
			ContentHash = BindingContentHash.Compute(path, tags, BindingKind.Plain, v, null),
			CreatedAt = now,
			UpdatedAt = now,
		}));
#pragma warning restore CA2016

		return new { id, path, tags };
	}

	// --- Helpers ---------------------------------------------------------

	static void AssertScope(IHttpContextAccessor accessor, string required)
	{
		var ctx = accessor.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(required, StringComparer.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}

	static void Require(string value, string name)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ArgumentException($"{name} is required");
	}

	static void RequireKey(string value, string name)
	{
		Require(value, name);
		if (!KeyRegex.IsMatch(value))
			throw new ArgumentException($"{name} '{value}' is invalid; must match ^[a-z][a-z0-9_-]{{0,99}}$");
	}
}
