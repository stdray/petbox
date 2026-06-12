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
	public const string TasksRead = "tasks:read";
	public const string TasksWrite = "tasks:write";
	public const string MemoryRead = "memory:read";
	public const string MemoryWrite = "memory:write";
	public const string LlmInvoke = "llm:invoke";
	public const string LlmAdmin = "llm:admin";
	public const string DeployRead = "deploy:read";
	public const string DeployWrite = "deploy:write";
	public const string AgentPoll = "agent:poll";
	public const string AgentHeartbeat = "agent:heartbeat";
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
		new(TasksRead,   "Read tasks",             "List/read task boards and plan nodes (and sessions) via the MCP tasks.*/session.* tools.", "Tasks"),
		new(TasksWrite,  "Write tasks",            "Create boards and upsert plan nodes / append sessions via the MCP tasks.*/session.* tools.", "Tasks"),
		new(MemoryRead,  "Read memory",            "List/read memory stores and entries via the MCP memory.* tools.", "Memory"),
		new(MemoryWrite, "Write memory",           "Create stores and upsert entries via the MCP memory.* tools.", "Memory"),
		new(LlmInvoke,   "Invoke LLM router",      "Call embed/rerank/chat through the router via the MCP llm.embed/rerank/chat tools.", "LlmRouter"),
		new(LlmAdmin,    "Manage LLM router",      "Read/write the router registry (endpoints, routes, api keys, cert pin) via the MCP llm.config_* tools.", "LlmRouter"),
		new(DeployRead,  "Read deploy fleet",      "List nodes and deployments via the deploy UI.", "Deploy"),
		new(DeployWrite, "Manage deploy fleet",    "Register nodes, mint node keys, create/move/start/stop deployments via the deploy UI.", "Deploy"),
		new(AgentPoll,   "Agent: poll desired state", "GET /agent/poll — a node-agent reads its assigned deployments. Issue only on node-scoped keys.", "Deploy"),
		new(AgentHeartbeat, "Agent: report state", "POST /agent/heartbeat — a node-agent reports actual container state. Issue only on node-scoped keys.", "Deploy"),
		new(AdminProvision, "Provision projects & keys", "ROOT-EQUIVALENT: mints API keys with ANY scopes for ANY project (including admin:provision itself), creates projects (project.*), sets config bindings (config.*). Issue deliberately; prefer short-lived keys for routine work.", "Admin"),
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
