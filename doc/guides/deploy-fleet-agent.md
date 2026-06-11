# Deploy control-plane — agent playbook

For an AI agent (e.g. one managing an `infra` project) asked to **put a service on the deploy rails** or operate the fleet. Operator-facing setup is in `doc/guides/deploy-fleet.md`; this is the tool-level recipe and the honest boundaries of what an agent can and cannot do.

## Capabilities & scope

You drive the fleet with the typed MCP `deploy.*` tools. They are **fleet-wide** (no per-project claim) and require your PetBox key to hold **`deploy:read`** (reads) / **`deploy:write`** (writes).

- Check first: `whoami` → if `scopes` lacks `deploy:write`, STOP and tell the human to grant it. Granting = mint a key with `deploy:read,deploy:write` (`apikey.create`, needs `admin:provision`), set it as the petbox MCP server's key, and **restart the client** (MCP caches scopes at connect — a new scope needs a fresh session). You cannot widen your own running session's scopes.

Tools: `deploy.node_list`, `deploy.node_upsert`, `deploy.node_delete`, `deploy.list`, `deploy.upsert`, `deploy.start`, `deploy.stop`, `deploy.move`, `deploy.delete`.

## What you CANNOT do (hand to a human)

- **Install the node-agent on a remote machine.** "Rails" need the agent running on the target host (Docker reconciler). Unless you have SSH to that box, a human must run the one-command bootstrap (`enroll.sh` — installs docker+caddy itself on supported Ubuntu LTS, `deploy-fleet.md §2`). Until a node is **enrolled and online**, `deploy.upsert` only records desired state — nothing reconciles it.
- **Auto-integrate CI.** There is no REST hook to bump the image digest from a build pipeline yet; you (or a human) set the new digest via `deploy.upsert` after the image is pushed.

## Recipe — migrate a service onto the rails

1. **Confirm a home exists.** `deploy.node_list` → is there an **online** node whose tags cover what the service needs (network egress, disk)?
   - No suitable/online node → STOP: ask the human to install+enroll the node-agent on the target machine (`deploy-fleet.md §2`), then resume. Don't fabricate a node — `deploy.node_upsert` registers a row, but with no agent it never runs anything.
2. **Know the image.** Get the pushed image ref/digest (e.g. `ghcr.io/you/bot:sha-…`) from the project's last build.
3. **Create the deployment:**
   ```
   deploy.upsert(service="<svc>", project="<petbox-project>", nodeId="<node>",
                 imageDigest="<image>", running=true,
                 requiredTags="<csv>", configTags="<csv>", relocatable=<bool>)
   ```
   - `project` drives server-side env resolution from `(project, configTags)` — make sure that project's config bindings exist if the container needs env.
   - `requiredTags` ⊆ the node's tags (also the failover constraint). `relocatable=true` only for stateless/idempotent services.
   - Needs ports/volumes/healthcheck/limits? Pass the **run-spec** fields on the same call: `ports=["127.0.0.1:8080:8080"]`, `volumes=["/host:/container[:ro]"]` (bind mounts; host dirs must exist), `restart=`, `healthcheckCmd/Interval/Timeout/Retries`, `memory=`, `cpus=`, `network=`, `command=[...]`, `labels=["k=v"]` (`petbox.*` reserved). Any run-spec change recreates the container (it's hashed). Details: `deploy-fleet.md §3b`.
   - **A web app behind the proxy?** Add `domain="app.example.com"` (+ optional `sitePort=`, default = ports[0] host port) — the agent maintains the Caddy route itself. The target node must have caddy (check the node's `capabilities` in `deploy.node_list`; enroll installs it on supported Ubuntu); a site on a caddy-less node shows an explicit `error` on the deployment instead of running silently broken (`deploy-fleet.md §3c`).
4. **Verify reconcile:** poll `deploy.list(service="<svc>")` until `actualState="Running"` and `healthy=true` (agent runs every ~30s). If it stays absent → check the node is online (`deploy.node_list`) and the image ref is valid (human checks `docker logs petbox-<svc>` on the node).

## Recipe — roll a new version
`deploy.upsert` the same `(service, node)` with the new `imageDigest`. The config-hash changes → the agent recreates the container. Rollback = `deploy.upsert` with the previous digest. Pause = `deploy.stop(id)`.

## Recipe — other ops
- Move to another node: `deploy.move(id, toNodeId)` (old agent self-fences, new one starts it).
- Second copy: `deploy.upsert` the same service on another node (one per node).
- Decommission: `deploy.delete(id)` (agent removes the container) or `deploy.node_delete(id)` (cascades a node's deployments).

## Safety notes
- **Idempotent by design** — re-running `deploy.upsert` with the same fields is a no-op for the agent (same config-hash). Safe to retry.
- **Failover = double-run, not data-loss** — only mark `relocatable` what tolerates two brief concurrent instances.
- **Don't enroll throwaway nodes you won't clean up** — `deploy.node_delete` when done; revoke any node key you minted out-of-band with `apikey.delete`.
- Report what you changed (node, service, image, desired state) and the verified `actualState` back to the human.
