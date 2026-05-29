namespace PetBox.Core.Auth;

// Canonical catalog of ApiKey scopes. Used by the create-key UI to render
// a checkbox group (so users don't have to type colon-separated strings
// and end up with typos like "log.query" or "onfig:read") and by the
// server to validate submitted scope sets — anything not in this list is
// rejected with a clear error.
public sealed record ApiKeyScope(string Value, string Title, string Description, string Module);

public static class ApiKeyScopes
{
	// Canonical scope identifiers — the single source of truth. Reference these
	// constants everywhere (policies, AssertScope/HasScope, catalog) instead of
	// repeating colon-separated string literals.
	public const string ConfigRead = "config:read";
	public const string ConfigWrite = "config:write";
	public const string LogsIngest = "logs:ingest";
	public const string LogsQuery = "logs:query";
	public const string LogsAdmin = "logs:admin";
	public const string HealthWrite = "health:write";
	public const string DataRead = "data:read";
	public const string DataWrite = "data:write";
	public const string DataSchema = "data:schema";
	public const string AdminProvision = "admin:provision";

	public static readonly IReadOnlyList<ApiKeyScope> All =
	[
		new(ConfigRead,  "Read shared config",     "GET /v1/conf (resolved config bundle).",                                       "Config"),
		new(ConfigWrite, "Write shared config",    "POST/PATCH bindings; create new bindings; edit secrets.",                     "Config"),
		new(LogsIngest,  "Ingest log events",      "POST /api/ingest/{p}/{log}/clef (CLEF JSON lines). Used by pets to ship log lines.", "Logs"),
		new(LogsQuery,   "Query logs (KQL)",       "KQL search via /api/logs/{p}/{log}/query and the MCP `log.query` tool; list logs.", "Logs"),
		new(LogsAdmin,   "Manage logs",            "Create and delete named logs via /api/logs/{p}/logs.",                        "Logs"),
		new(HealthWrite, "Push health/status",     "POST /api/health — push a service status report (svc + tags + version/status).", "Health"),
		new(DataRead,    "Read DataDbs",           "List DataDbs, SELECT via /api/data/{p}/{db}/query, describe schemas.",       "Data"),
		new(DataWrite,   "Write DataDb rows",      "INSERT/UPDATE/DELETE via /api/data/{p}/{db}/exec.",                          "Data"),
		new(DataSchema,  "Schema apply / lifecycle","CREATE/DROP tables, apply DbUp migrations, create/delete DataDbs.",          "Data"),
		new(AdminProvision, "Provision projects & keys", "Agent onboarding: create projects/services, mint API keys, set config bindings via MCP. Issue only on short-lived agent keys.", "Admin"),
	];

	static readonly HashSet<string> Allowed =
		new(All.Select(s => s.Value), StringComparer.Ordinal);

	// Returns (valid, invalid) splits of a comma/space-separated scope string.
	// Used by the create-key handler to reject typos like "log.query".
	public static (List<string> Valid, List<string> Invalid) Validate(string? input)
	{
		var valid = new List<string>();
		var invalid = new List<string>();
		if (string.IsNullOrWhiteSpace(input))
			return (valid, invalid);

		var parts = input.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		foreach (var p in parts)
		{
			if (Allowed.Contains(p))
				valid.Add(p);
			else
				invalid.Add(p);
		}
		return (valid, invalid);
	}
}
