namespace PetBox.Core.Auth;

// How far a scope reaches RELATIVE TO THE TENANT AXIS. This is the classification the catalog was
// missing (work `workspaceadmin-self-issue-admin-provision-root`, spec `access-root-explicit`): a
// root-equivalent capability used to sit in one flat list beside `memory:read` and render as the
// same checkbox, so "issue it only deliberately" was a sentence in a Description and nothing else.
//
// THE CRITERION IS NOT TASTE — it is read off the code. A scope is `Privileged` exactly when every
// surface that consumes it is declared [TenantExempt(Provisioning|FleetWide)]: the project/workspace
// claim provably does not confine it, so holding it reaches other tenants. A `Tenant` scope's
// surfaces all declare [TenantFrom(...)] (or are project-routed REST), so the claim IS the boundary
// and a workspace admin issuing one can only ever reach their own tenant.
//
// Destructiveness is deliberately NOT the axis. `data:schema` drops tables and `logs:admin` deletes
// logs, but both are confined to the caller's project — a tenant wrecking their own data is their
// business. What this enum grades is whether the blast radius escapes the tenant who issued the key.
public enum ScopeAuthority
{
	// Confined by the caller's project/workspace claim. Any admin of a tenant may issue it.
	Tenant,

	// Escapes the tenant axis — cross-tenant reach or a fleet-wide control plane. Only an issuer who
	// already holds that reach may hand it out (see KeyIssuer / AgentKeyAdminService's grant gate).
	Privileged,
}

// Canonical catalog of ApiKey scopes. Used by the create-key UI to render
// a checkbox group (so users don't have to type colon-separated strings
// and end up with typos like "log_query" or "onfig:read") and by the
// server to validate submitted scope sets — anything not in this list is
// rejected with a clear error.
//
// `Authority` defaults to Tenant so adding a scope is safe by omission: a new entry is confined
// until someone deliberately types Privileged, never privileged because someone forgot a flag.
public sealed record ApiKeyScope(
	string Value,
	string Title,
	string Description,
	string Module,
	ScopeAuthority Authority = ScopeAuthority.Tenant);

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
	public const string HealthRead = "health:read";
	public const string DataRead = "data:read";
	public const string DataWrite = "data:write";
	public const string DataSchema = "data:schema";
	public const string TasksRead = "tasks:read";
	public const string TasksWrite = "tasks:write";
	public const string TasksApprove = "tasks:approve";
	// Changing the RULES that already govern EXISTING nodes — deliberately separate from
	// tasks:write (which writes nodes UNDER the rules). See the catalog entry below.
	public const string MethodologyWrite = "methodology:write";
	public const string MemoryRead = "memory:read";
	public const string MemoryWrite = "memory:write";
	public const string LlmInvoke = "llm:invoke";
	public const string LlmAdmin = "llm:admin";
	public const string DeployRead = "deploy:read";
	public const string DeployWrite = "deploy:write";
	public const string AgentPoll = "agent:poll";
	public const string AgentHeartbeat = "agent:heartbeat";
	// Portable agent-definition store (NOT the deploy node-agent poll/heartbeat pair).
	public const string AgentsRead = "agents:read";
	public const string AgentsWrite = "agents:write";
	public const string AdminProvision = "admin:provision";

	public static readonly IReadOnlyList<ApiKeyScope> All =
	[
		new(ConfigRead,  "Read shared config",     "GET /v1/conf (resolved config bundle).",                                       "Config"),
		new(ConfigWrite, "Write shared config",    "POST/PATCH bindings; create new bindings; edit secrets.",                     "Config"),
		new(LogsIngest,  "Ingest log events",      "POST /api/ingest/{p}/{log}/clef (CLEF JSON lines). Used by pets to ship log lines.", "Logs"),
		new(LogsQuery,   "Query logs (KQL)",       "KQL search via /api/logs/{p}/{log}/query and the MCP `log_query` tool; list logs.", "Logs"),
		new(LogsAdmin,   "Manage logs",            "Create and delete named logs via /api/logs/{p}/logs.",                        "Logs"),
		new(HealthWrite, "Push health/status",     "POST /api/health — push a service status report (svc + tags + version/status).", "Health"),
		new(HealthRead,  "Read health/status",      "Read the latest health report per service (+ optional history) via the MCP health_search tool.", "Health"),
		new(DataRead,    "Read DataDbs",           "List DataDbs, SELECT via /api/data/{p}/{db}/query, describe schemas.",       "Data"),
		new(DataWrite,   "Write DataDb rows",      "INSERT/UPDATE/DELETE via /api/data/{p}/{db}/exec.",                          "Data"),
		new(DataSchema,  "Schema apply / lifecycle","CREATE/DROP tables, apply DbUp migrations, create/delete DataDbs.",          "Data"),
		new(TasksRead,   "Read tasks",             "List/read task boards and task nodes (and sessions) via the MCP tasks_*/session_* tools.", "Tasks"),
		new(TasksWrite,  "Write tasks",            "Create boards and upsert task nodes / append sessions via the MCP tasks_*/session_* tools.", "Tasks"),
		new(TasksApprove, "Approve tasks",         "Perform approval-gated workflow transitions the methodology ENFORCES (enforceApproval) — the maintainer capability. Meaningless without tasks:write.", "Tasks"),
		new(MethodologyWrite, "Govern the live process", "GOVERNANCE: any act over an EXISTING live process — rewrite its rules (methodology_rules_upsert), author a live rules document (methodology_create), move an existing board under other rules (board_adopt), retire the whole process (methodology_close), add/retire/destroy/rewire one of its process-role boards (board_close, board_reopen, board_delete, board_set_wire), or move the active-instance pointer the agent-facing guide resolves through (methodology_set_active). tasks:write writes nodes UNDER the process; this changes the process itself. NOT needed for inert templates (template_upsert/snapshot/delete — documents, not processes), for board_create (constrained BY the rules, changes nothing that exists), or for methodology_set_description (prose only — it cannot touch structure, and the guide derives every invariant from structure). Meaningless without tasks:write.", "Tasks"),
		new(MemoryRead,  "Read memory",            "List/read memory stores and entries via the MCP memory_* tools.", "Memory"),
		new(MemoryWrite, "Write memory",           "Create stores and upsert entries via the MCP memory_* tools.", "Memory"),
		new(LlmInvoke,   "Invoke LLM router",      "Call embed/rerank/chat through the router via the MCP llm_embed/rerank/chat tools.", "LlmRouter"),
		new(LlmAdmin,    "Manage LLM router",      "Read/write the router registry (endpoints, routes, api keys, cert pin) via the MCP llm_config_* tools.", "LlmRouter"),
		// PRIVILEGED — DeployTools is [TenantExempt(FleetWide)] and says so in its own tool text:
		// deploy_node_list returns "ALL nodes across ALL projects, not just yours: there is no
		// `projectKey` parameter and none is accepted as a filter", and deploy_list spans "every
		// project's deployments". A read that enumerates the whole fleet's hosts and running services
		// is cross-tenant reconnaissance, so it is not a workspace admin's to hand out.
		new(DeployRead,  "Read deploy fleet",      "FLEET-WIDE: lists nodes and deployments across ALL projects — the deploy control-plane has no project/workspace scoping, so there is no projectKey to filter by.", "Deploy", ScopeAuthority.Privileged),
		new(DeployWrite, "Manage deploy fleet",    "NEAR-ROOT, FLEET-WIDE: no project/workspace scoping exists on the deploy control-plane — register nodes, mint node keys, create/move/start/stop deployments for ANY project via the deploy UI.", "Deploy", ScopeAuthority.Privileged),
		// PRIVILEGED — the two node-agent scopes. /agent/poll and /agent/heartbeat are
		// [TenantExempt(FleetWide)] and resolve the NODE from the key's own `project` claim, so the
		// claim names a fleet node rather than bounding one. "Issue only on node-scoped keys" was the
		// unstated assumption; marking them makes it enforced. Node keys are unaffected — they are
		// minted by MintNodeKeyAsync (the enroll/rotate path), which never takes caller-chosen scopes.
		new(AgentPoll,   "Agent: poll desired state", "FLEET-WIDE node-agent scope: GET /agent/poll reads a NODE's assigned deployments (the node comes from the key's own claim, not from a tenant) — including other projects' resolved container env. Issued automatically on node keys; not a tenant scope.", "Deploy", ScopeAuthority.Privileged),
		new(AgentHeartbeat, "Agent: report state", "FLEET-WIDE node-agent scope: POST /agent/heartbeat reports a NODE's actual container state (the node comes from the key's own claim, not from a tenant). Issued automatically on node keys; not a tenant scope.", "Deploy", ScopeAuthority.Privileged),
		new(AgentsRead,  "Read agent definitions", "List/get portable agent-definition documents (roles/tier/capabilities/spawn/escalation) via /api/{p}/agent-defs and the MCP agent_def_* tools.", "Agents"),
		new(AgentsWrite, "Write agent definitions","Create/update/delete portable agent-definition documents via /api/{p}/agent-defs and the MCP agent_def_* tools.", "Agents"),
		// PRIVILEGED — the root-equivalent one. ApiKeyTools/ProjectTools/ConfigTools are all
		// [TenantExempt(Provisioning)]: this scope mints keys into ANY project (itself included),
		// so holding it is holding every other scope in every tenant.
		new(AdminProvision, "Provision projects & keys", "ROOT-EQUIVALENT: mints API keys with ANY scopes for ANY project (including admin:provision itself), creates projects (project_create), sets config bindings (config_binding_upsert). Issue deliberately; prefer short-lived keys for routine work.", "Admin", ScopeAuthority.Privileged),
	];

	static readonly HashSet<string> Allowed =
		new(All.Select(s => s.Value), StringComparer.Ordinal);

	// DERIVED from the catalog, never a second hand-maintained list — a privileged scope added to
	// `All` is gated the moment it is added, and cannot be forgotten here.
	static readonly HashSet<string> PrivilegedValues =
		new(All.Where(s => s.Authority == ScopeAuthority.Privileged).Select(s => s.Value), StringComparer.Ordinal);

	// An UNKNOWN scope is not privileged — it is not grantable at all (Validate rejects it first).
	// Answering "true" here would turn every typo into a confusing privilege refusal instead of the
	// "unknown scope" message the caller needs.
	public static bool IsPrivileged(string scope) => PrivilegedValues.Contains(scope);

	// The privileged members of a submitted set, in catalog order — what a refusal names back to the
	// caller so the message says WHICH scope was the problem rather than merely that one was.
	public static IReadOnlyList<string> PrivilegedIn(IEnumerable<string> scopes)
	{
		var set = new HashSet<string>(scopes, StringComparer.Ordinal);
		return [.. All.Where(s => s.Authority == ScopeAuthority.Privileged && set.Contains(s.Value)).Select(s => s.Value)];
	}

	// The catalog an issuer may actually choose from — what the checkbox groups render. UI ONLY:
	// the server gate in AgentKeyAdminService is what actually holds, because a forged POST never
	// asks this method anything.
	public static IReadOnlyList<ApiKeyScope> GrantableBy(bool mayGrantPrivileged) =>
		mayGrantPrivileged ? All : [.. All.Where(s => s.Authority == ScopeAuthority.Tenant)];

	// Returns (valid, invalid) splits of a comma/space-separated scope string.
	// Used by the create-key handler to reject typos like "log_query".
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
