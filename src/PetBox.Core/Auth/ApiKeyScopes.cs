namespace PetBox.Core.Auth;

// Canonical catalog of ApiKey scopes. Used by the create-key UI to render
// a checkbox group (so users don't have to type colon-separated strings
// and end up with typos like "log.query" or "onfig:read") and by the
// server to validate submitted scope sets — anything not in this list is
// rejected with a clear error.
public sealed record ApiKeyScope(string Value, string Title, string Description, string Module);

public static class ApiKeyScopes
{
	public static readonly IReadOnlyList<ApiKeyScope> All =
	[
		new("config:read",  "Read shared config",     "GET /api/config/{ws}/resolve and the bindings list.",                          "Config"),
		new("config:write", "Write shared config",    "POST/PATCH bindings; create new bindings; edit secrets.",                     "Config"),
		new("logs:ingest",  "Ingest log events",      "POST /api/ingest/clef (CLEF JSON lines). Used by pets to ship log lines.",   "Logs"),
		new("logs:query",   "Query logs (KQL)",       "KQL search via /api/logs/{p}/query and the MCP `log.query` tool.",           "Logs"),
		new("data:read",    "Read DataDbs",           "List DataDbs, SELECT via /api/data/{p}/{db}/query, describe schemas.",       "Data"),
		new("data:write",   "Write DataDb rows",      "INSERT/UPDATE/DELETE via /api/data/{p}/{db}/exec.",                          "Data"),
		new("data:schema",  "Schema apply / lifecycle","CREATE/DROP tables, apply DbUp migrations, create/delete DataDbs.",          "Data"),
		new("admin:provision", "Provision projects & keys", "Agent onboarding: create projects/services, mint API keys, set config bindings via MCP. Issue only on short-lived agent keys.", "Admin"),
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
