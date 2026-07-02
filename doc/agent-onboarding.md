# Agent onboarding

How a coding agent (Claude Code, etc.) bootstraps a pet onto PetBox using a temporary
**agent key**. The agent creates the project, services, downstream API keys, and config
bindings — then the agent key expires.

## 1. Human issues an agent key

Sysadmin → **`/ui/admin/sys/agent-keys`** → *Issue agent key*:

- **Scopes**: keep `admin:provision` checked (lets the agent call the provisioning MCP tools).
  **`admin:provision` is ROOT-EQUIVALENT** — it mints keys with any scopes for any project,
  including `admin:provision` itself. Issue it deliberately; prefer a short TTL.
- **TTL (hours)**: e.g. `24`. The key auto-expires; expired keys return `401`. Leave empty for
  a non-expiring key — the lifetime is the issuer's deliberate choice.
- **Project key**: nominal owner, default `$system` (provisioning tools are cross-project, so this
  is just for bookkeeping). **All projects** mints a cross-project key (claim `*`) that reads and
  writes every project — for maintenance/monitoring.

Copy the `yb_key_…` value — it's shown once.

## 2. Agent connects over MCP

Point the agent's MCP client at `https://<petbox-host>/mcp` with header
`X-Api-Key: <agent-key>`. The provisioning tools require the `admin:provision` scope.

## 3. Provisioning tools

Provisioning is done through the typed per-type tools (the generic `entity.*` family is
gone). The provisioning tools (`project_*`, `apikey_*`, `config_*`) require the
`admin:provision` scope.

| Call | Purpose |
|---|---|
| `project_create({ workspaceKey, key, name, description? })` | Create the pet's project. |
| `apikey_create({ name, scopes, projectKey?, expiresInSeconds?, allProjects? })` | Mint the pet's **production** key. Returns the raw key once — hand it to the pet. Omit `expiresInSeconds` for a non-expiring key. `allProjects:true` (omit `projectKey`) mints a cross-project `*` key. |
| `config_binding_upsert({ workspaceKey, path, value, tags })` | Seed config (PUT by (path, tagset) — an identical twin is superseded). `tags` must include `ws:{workspaceKey}`. |
| `project_list` / `apikey_list` / `apikey_delete` / `config_binding_list` / `config_binding_delete` | List / remove. `project` has no delete. |

Project-scoped types `db` and `log` (created with the minted key, gated on
`data:schema` / `logs:admin`) plus the operational tools `data_schema_apply`,
`data_query`, `data_exec`, `log_query`, and `db_describe` round out the surface.

## 4. Typical flow

1. `project_create({ workspaceKey: "myws", key: "kpvotes", name: "KpVotes" })`
2. `apikey_create({ projectKey: "kpvotes", name: "kpvotes-ts prod", scopes: "config:read,data:read,data:write,data:schema" })` → **save the returned key**
3. `db_create({ projectKey: "kpvotes", name: "kpvotes-cache" })` (using the minted key)
4. `data_schema_apply({ projectKey: "kpvotes", dbName: "kpvotes-cache", name: "M001_votes", sql: "CREATE TABLE votes (...)" })`
5. `config_binding_upsert({ workspaceKey: "myws", path: "kpvotes.kp-uri", value: "...", tags: "ws:myws,project:kpvotes" })`
6. Give the pet its production key + endpoint; the agent key expires on its own.

## Errors

- `ApiKey lacks required scope 'admin:provision'` → the agent key wasn't issued with the scope.
- `401` on any call → the agent key expired; issue a fresh one.
- `Project '…' already exists` / `Workspace '…' not found` → fix the key/workspace args.

## Status

- ✅ 27.1 agent-key infra (`ApiKey.ExpiresAt`, expiry enforcement, sysadmin UI)
- ✅ 27.2 provisioning MCP tools + `admin:provision` scope
- ✅ 27.3 this doc
- ⏳ 27.4 / 27.5 live dogfooding against the deployed instance + gotcha capture
