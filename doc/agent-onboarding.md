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

Provisioning is done through the generic **`entity.*`** tools with a `type`
discriminator. The provisioning types (`project`, `apikey`, `config_binding`)
require the `admin:provision` scope.

| Call | Purpose |
|---|---|
| `entity.create({ type: "project", props: { workspaceKey, key, name, description? } })` | Create the pet's project. |
| `entity.create({ type: "apikey", props: { projectKey, name, scopes, expiresInSeconds? } })` | Mint the pet's **production** key. Returns the raw key once — hand it to the pet. Omit `expiresInSeconds` for a non-expiring key. |
| `entity.create({ type: "config_binding", props: { workspaceKey, path, value, tags } })` | Seed config. `tags` must include `ws:{workspaceKey}`. |
| `entity.list({ type, filter })` / `entity.delete({ type, key })` | List / remove entities. `project` has no delete. |

Project-scoped entity types `db` and `log` (created with the minted key, gated on
`data:schema` / `logs:admin`) plus the operational tools `data.schema_apply`,
`data.query`, `data.exec`, `log.query`, and `entity.describe({ type: "db" })`
round out the surface.

## 4. Typical flow

1. `entity.create({ type: "project", props: { workspaceKey: "myws", key: "kpvotes", name: "KpVotes" } })`
2. `entity.create({ type: "apikey", props: { projectKey: "kpvotes", name: "kpvotes-ts prod", scopes: "config:read,data:read,data:write,data:schema" } })` → **save the returned key**
3. `entity.create({ type: "db", props: { projectKey: "kpvotes", name: "kpvotes-cache" } })` (using the minted key)
4. `data.schema_apply({ projectKey: "kpvotes", dbName: "kpvotes-cache", name: "M001_votes", sql: "CREATE TABLE votes (...)" })`
5. `entity.create({ type: "config_binding", props: { workspaceKey: "myws", path: "kpvotes.kp-uri", value: "...", tags: "ws:myws,project:kpvotes" } })`
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
