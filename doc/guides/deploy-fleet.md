# Deploy control-plane — operator guide

How to run your Docker services on the PetBox **deploy control-plane**: PetBox holds the *desired state* (which service runs on which machine, from which image), and a thin **node-agent** on each machine reconciles Docker to match. The agent talks to PetBox **outbound-only** (HTTPS poll + heartbeat) — no inbound port, so it works behind NAT, firewalls, and on a laptop/WSL2.

This guide takes you from zero to a running service: get a key → install the agent on a machine → put a service on the rails → operate it.

## 0. Concepts (30 seconds)

- **Node** — a machine in your fleet, identified by a slug (`vdsina-1`, `local-pc`) and capability **tags** (e.g. `net.x`, `disk=nvme`). Tags are how a deployment says "I need a machine that can reach X".
- **Deployment** — the desired state of one service on one node: image, running/stopped, required tags. One copy per (service, node).
- **node-agent** — the small Python reconciler on each machine. Polls `/agent/poll`, runs/stops/recreates containers to match, reports actual state via `/agent/heartbeat`.

## 1. Get a deploy key (operator key)

Managing the fleet needs an API key with scopes **`deploy:read,deploy:write`**.

**Via MCP** (if your PetBox key has `admin:provision`):
```
apikey_create(projectKey="$system", name="fleet-ops", scopes="deploy:read,deploy:write")
```
The raw key is shown **once** — store it. (Add `admin:provision` too if the same key should also mint other keys.)

**Via the UI:** Sysadmin → **Agent keys** (`/ui/admin/sys/agent-keys`) → issue a key with the `deploy:read` + `deploy:write` scopes checked. (UI keys carry a TTL; for a long-lived ops key prefer the MCP path above, which is non-expiring.)

You do **not** create node keys by hand — they are minted automatically when you enroll a node (next step).

## 2. Install the node-agent on a machine

The agent files live in the repo under `agent/`: `petbox_deploy_agent.py`, `enroll.sh`, `petbox-deploy-agent.service`.

### 2a. A bare Ubuntu server (the normal case) — one command

`enroll.sh` IS the bare-server setup: on a supported OS (**Ubuntu LTS 22.04 / 24.04 / 26.04** — a fixed short list; anything else is an explicit refusal, `PETBOX_SKIP_OS_CHECK=1` to override) it installs docker and caddy if missing, wires the Caddyfile to `/etc/caddy/petbox.d`, registers the node + mints its key, installs and starts the agent's systemd unit. Idempotent — re-running re-checks everything and rotates the node key.

```sh
# copy the agent dir to the box (or clone the repo), then:
scp -r agent/ user@new-server:
ssh user@new-server
PETBOX_URL=https://petbox.3po.su \
PETBOX_ADMIN_KEY=<your deploy:write key from step 1> \
  sudo -E ./agent/enroll.sh <node-id> "<tags-csv>"
# e.g. sudo -E ./agent/enroll.sh vdsina-1 "net.x,disk=nvme"

journalctl -u petbox-deploy-agent -f      # watch it poll
```

That's the whole flow: once the node connects, PetBox brings up whatever deployments are assigned to it — no per-deployment actions on the host.

> **`sudo` note:** `enroll.sh` installs packages and writes `/etc`. Run it as **root** or where `sudo` is passwordless (`sudo -E` keeps the PETBOX_* env vars). A non-interactive run with a password-prompting `sudo` will hang.

> **Operator pre-checks** (before trusting a fresh box): you can SSH in, and the host's security posture is what you intend. The agent **reports** the posture in its heartbeat — root SSH login not disabled / password auth allowed / low memory / low disk show up as ⚠ warnings on the node row (UI + `deploy_node_list`), and every warning appear/clear is logged to the `$system` self-log — but it deliberately does NOT change sshd config (report-only; fixing it is the operator's call).

The node now appears in the fleet (UI `/ui/admin/sys/deploy` or `deploy_node_list`) and goes **online** within a poll interval (~30s). `journalctl -u petbox-deploy-agent -f` shows one `reconciled: N desired, M action(s), …; heartbeat ok` line per cycle.

### 2b. Local test in WSL2 (Windows + Docker Desktop)

WSL2 distros don't have `docker` until you expose it. Two options:

- **Easiest** — Docker Desktop → Settings → **Resources → WSL Integration** → enable for your distro (e.g. `Ubuntu-26.04`), Apply & Restart. Now `docker` works inside the distro.
- **Or** install Docker Engine directly in the distro and enable systemd: add `[boot]\nsystemd=true` to `/etc/wsl.conf`, `wsl --shutdown`, reopen, then `curl -fsSL https://get.docker.com | sh`.

Then run the same steps as 2a inside the distro, and tag the node `ephemeral` (a laptop comes and goes):
```sh
PETBOX_URL=https://petbox.3po.su PETBOX_ADMIN_KEY=<deploy key> \
  ./agent/enroll.sh local-pc "net.x" --ephemeral
```

> No systemd in WSL2 and don't want to enable it? For a quick test just run the agent in the foreground:
> `PETBOX_URL=https://petbox.3po.su PETBOX_NODE_KEY=<minted key> python3 agent/petbox_deploy_agent.py`
> (the minted key is in `/etc/petbox-deploy-agent.env` after enroll, or from `deploy_node_upsert`).

## 3. Put a service on the rails

Create a deployment — the agent on that node will pull the image and run it.

**Via the UI** `/ui/admin/sys/deploy`: fill the *New deployment* form (service, project, node, image, optional required/config tags) → Create.

**Via MCP:**
```
deploy_upsert(service="bot", projectKey="yobapub", nodeId="vdsina-1",
              imageDigest="ghcr.io/you/bot:sha-abc123", running=true,
              requiredTags="net.x", configTags="env:prod")
```
- `projectKey` — the PetBox project whose **config** applies; the agent's container env is resolved **server-side** from `(project, configTags)` via the same `/v1/conf` resolver, so the node key needs no `config:read`.
- `requiredTags` — the node's tags must cover these (also used when failover picks a new home).
- `relocatable=true` — let failover move it to another matching node if this one goes silent.

Within a poll the container `petbox-<service>` is up. Check actual state: UI grid, or `deploy_list` (shows desired + last reported `actualState`/`healthy`).

### 3b. Run-spec: ports, volumes and the rest of `docker run`

A deployment can carry a declarative **run-spec** — the compose-subset the agent maps 1:1 to `docker run` flags (env is *not* part of it; env stays config-resolved). All fields are optional:

```
deploy_upsert(service="web", projectKey="yobapub", nodeId="vdsina-1",
              imageDigest="ghcr.io/you/web:sha-abc123",
              ports=["127.0.0.1:8080:8080"],                 # [ip:]host:container[/tcp|udp]
              volumes=["/opt/web/logs:/app/logs",            # /host:/container[:ro|rw] (bind mounts only)
                       "/opt/web/keys:/app/keys"],
              restart="unless-stopped",                      # no|on-failure|unless-stopped|always
              healthcheckCmd="curl -f http://localhost:8080/health",
              healthcheckInterval="30s", healthcheckTimeout="5s", healthcheckRetries=3,
              memory="256m", cpus=0.5,                       # docker --memory / --cpus
              network="bridge",                              # bridge|host|none|<name>
              command=["python", "-m", "web"],               # CMD override
              labels=["team=infra"])                         # extra labels; petbox.* is reserved
```

The UI's *New deployment* form covers ports/volumes/restart/memory/cpus/network/site-domain; healthcheck, command and labels are MCP-only. The run-spec is part of the config-hash, so **changing any field recreates the container** on the next poll. Host directories for volumes must exist (create/chown them when onboarding the service); the spec is structurally allowlisted — there is no `--privileged`/`--cap-add`/raw-args escape.

### 3c. Sites: a web app behind the node's reverse proxy (Caddy)

A deployment with a **`domain`** is a *site*: besides the container, the node agent keeps a Caddy route (domain → loopback port) in line with it.

```
deploy_upsert(service="web", projectKey="yobapub", nodeId="vdsina-1",
              imageDigest="ghcr.io/you/web:sha-abc123",
              ports=["127.0.0.1:8080:8080"],
              domain="app.example.com")          # sitePort defaults to 8080 (ports[0] host port)
```

- The agent owns `/etc/caddy/petbox.d/<service>.caddy` (`domain { reverse_proxy 127.0.0.1:<port> }`) and runs `systemctl reload caddy` on any change. It never touches anything outside that include dir.
- **Host prerequisite**: caddy installed and the Caddyfile carrying `import /etc/caddy/petbox.d/*.caddy` — `enroll.sh` sets both up on a supported OS (§2a); only hand-provisioned hosts need it done manually.
- Stopping/deleting the site removes its route (Caddy stops serving the domain instead of proxying into a dead container).
- The agent reports host **capabilities** (`docker,caddy`) in its heartbeat — visible in the nodes table. A site assigned to a node **without caddy** surfaces as an explicit per-deployment error in the grid/`deploy_list` (`error: "site route not applied: caddy is not available on this node"`), not a silent failure. Reconcile errors in general (failed `docker run` included) now land in that same `error` field.

## 3a. Migrating a service that bootstraps from PetBox

Many PetBox services bootstrap from PetBox itself — they need `PETBOX_ENDPOINT` + `PETBOX_API_KEY` in their env to pull their own config/log/etc. Previously those lived only in the project's **CI secrets**, so the server-side env resolve had nothing to hand the container. Put them in config so the rails can deliver them:

1. Create two **config bindings** in the service's workspace, tagged so they only resolve for this deployment (not the app's normal `/v1/conf`):
   - `PETBOX_ENDPOINT` — **Plain**, tags `ws:<ws>,project:<proj>,deploy`
   - `PETBOX_API_KEY` — **Secret** (AES-encrypted at rest), tags `ws:<ws>,project:<proj>,deploy`
2. Give the deployment **`configTags="deploy"`** (`deploy_upsert(... configTags="deploy")`).

Now the poll's server-side resolve over `(project, ["deploy"])` injects both into the container's `--env-file`. Because they're tagged `deploy`, they're scoped to the deploy path and don't leak into the application's own `/v1/conf` reads. (Worked example: `kpvotes` — bootstraps from PetBox, migrated exactly this way.)

## 4. Updating the running version (deploys)

To roll a new image, set the new digest on the deployment — `deploy_upsert` with the same `(service, node)` and the new `imageDigest` (or edit it in the UI). The agent notices the changed config-hash and recreates the container.

> CI integration (auto-bump the digest from a GitHub Actions build) is **not yet turn-key** — there's no REST hook for it. Today the new digest is set via the UI, `deploy_upsert` (MCP), or an agent/script that calls the MCP tool after the image is pushed. A first-class CI hook is a planned follow-up.

## 5. Day-2 operations

- **Status / list** — UI grid or `deploy_list` (per node/service filter); `deploy_node_list` for the fleet (incl. agent-detected `capabilities`, the `host` snapshot — memory/disk/os/ssh-posture — and computed `warnings`).
- **Host health** — the agent heartbeats a host report every cycle; thresholds live server-side (low memory: <10% or <150 MB; low disk: <10% or <2 GB). Warning transitions (appeared/cleared) are logged to the `$system` self-log, so history needs no separate monitoring stack.
- **Stop / start** — `deploy_stop(id)` / `deploy_start(id)` or the UI buttons (sets desired state; the agent reconciles).
- **Move** — `deploy_move(id, toNodeId)` (the source agent self-fences the old container, the target agent starts it).
- **Copies** — create a deployment of the same service on another node (one per node).
- **Remove** — `deploy_delete(id)` removes the deployment (the owning agent then removes the container); `deploy_node_delete(id)` removes a node and cascades its deployments.

## 6. Failover (auto-move)

If a node stops reporting for ~90s (3 missed polls), a background sweeper relocates its **`relocatable`** deployments to an online node whose tags cover the deployment's `requiredTags`. Failure mode is a brief **double-run** (not data loss): when the silent node returns, its agent sees it no longer owns the deployment and self-stops the container. Mark only stateless / idempotent services `relocatable`.

## 7. Troubleshooting

- **Node stays offline** — agent not running (`systemctl status petbox-deploy-agent`) or can't reach PetBox (outbound HTTPS to `petbox.3po.su` blocked). Check `journalctl -u petbox-deploy-agent`.
- **403 on deploy tools** — your key lacks `deploy:read/write` (step 1). MCP caches scopes at connect — after changing a key, reconnect/restart the client.
- **Container won't start** — check the image ref and that the env resolves (project + configTags); `docker logs petbox-<service>` on the node.
- **"one copy per node"** — a service already has a deployment on that node; move or delete it first.
- **Container logs in PetBox** — not shipped by the agent yet (heartbeat reports state only); use `docker logs` on the node for now.

For an AI agent driving migrations, see `doc/guides/deploy-fleet-agent.md`.
