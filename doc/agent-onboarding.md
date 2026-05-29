# Agent onboarding

How a coding agent (Claude Code, etc.) bootstraps a pet onto PetBox using a temporary
**agent key**. The agent creates the project, services, downstream API keys, and config
bindings — then the agent key expires.

## 1. Human issues an agent key

Sysadmin → **`/ui/admin/sys/agent-keys`** → *Issue agent key*:

- **Scopes**: keep `admin:provision` checked (lets the agent call the provisioning MCP tools).
- **TTL (hours)**: e.g. `24`. The key auto-expires; expired keys return `401`.
- **Project key**: nominal owner, default `$system` (provisioning tools are cross-project, so this
  is just for bookkeeping).

Copy the `yb_key_…` value — it's shown once.

## 2. Agent connects over MCP

Point the agent's MCP client at `https://<petbox-host>/mcp` with header
`X-Api-Key: <agent-key>`. The provisioning tools require the `admin:provision` scope.

## 3. Provisioning tools

| Tool | Purpose |
|---|---|
| `workspace.create_project({workspaceKey, key, name, description?})` | Create the pet's project. |
| `project.create_service({projectKey, key, healthModel?, url?})` | Register a service (`healthModel`: `push` or `endpoint`). |
| `project.create_apikey({projectKey, name, scopes, expiresInSeconds?})` | Mint the pet's **production** key. Returns the raw key once — hand it to the pet. Omit `expiresInSeconds` for a non-expiring key. |
| `project.set_config_binding({workspaceKey, path, value, tags})` | Seed config. `tags` must include `ws:{workspaceKey}`. |

Data-module tools (`data.create_db`, `data.schema_apply`, `data.query`, `data.exec`) are also
available once the minted key carries `data:*` scopes.

## 4. Typical flow

1. `workspace.create_project({ workspaceKey: "myws", key: "kpvotes", name: "KpVotes" })`
2. `project.create_service({ projectKey: "kpvotes", key: "kpvotes-ts", healthModel: "push" })`
3. `project.create_apikey({ projectKey: "kpvotes", name: "kpvotes-ts prod", scopes: "config:read,data:read,data:write,data:schema" })` → **save the returned key**
4. `data.create_db({ projectKey: "kpvotes", name: "kpvotes-cache" })` (using the minted key)
5. `data.schema_apply({ projectKey: "kpvotes", dbName: "kpvotes-cache", name: "M001_votes", sql: "CREATE TABLE votes (...)" })`
6. `project.set_config_binding({ workspaceKey: "myws", path: "kpvotes.kp-uri", value: "...", tags: "ws:myws,project:kpvotes" })`
7. Give the pet its production key + endpoint; the agent key expires on its own.

## Errors

- `ApiKey lacks required scope 'admin:provision'` → the agent key wasn't issued with the scope.
- `401` on any call → the agent key expired; issue a fresh one.
- `Project '…' already exists` / `Workspace '…' not found` → fix the key/workspace args.

## Status

- ✅ 27.1 agent-key infra (`ApiKey.ExpiresAt`, expiry enforcement, sysadmin UI)
- ✅ 27.2 provisioning MCP tools + `admin:provision` scope
- ✅ 27.3 this doc
- ⏳ 27.4 / 27.5 live dogfooding against the deployed instance + gotcha capture
