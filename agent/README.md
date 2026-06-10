# PetBox deploy node-agent

A thin pull-based reconciler that runs on each fleet machine. It polls PetBox for the
deployments assigned to this node, drives Docker to match, and reports actual state via
heartbeat. **Outbound HTTPS only** — no inbound port, so it works behind NAT / firewalls
and on a laptop (incl. WSL2). Zero third-party deps (stdlib `urllib` + `subprocess`).

## How it works

Every `POLL_INTERVAL` seconds:

1. `GET /agent/poll` → the desired deployments for this node (image, desired state,
   `configHash`, and the **env resolved server-side** from the deployment's project config).
2. List petbox-managed containers (`docker ps` by label) = actual state.
3. `plan_actions(desired, actual)` decides the minimal start/stop/recreate set:
   - recreate when `configHash` changed or the container isn't running;
   - **self-fence**: remove any managed container no longer assigned here (e.g. after a
     failover relocation) — bounds the double-run window.
4. `POST /agent/heartbeat` ← actual container states (also bumps the node's liveness).

The reconcile decision is a pure function (`plan_actions`), unit-tested in
`test_reconcile.py` without Docker:

```sh
python3 -m unittest test_reconcile -v
```

## Install on an Ubuntu node

```sh
# 1. copy the agent
sudo mkdir -p /opt/petbox-deploy-agent
sudo cp petbox_deploy_agent.py /opt/petbox-deploy-agent/

# 2. enroll: registers the node + mints its key + writes /etc/petbox-deploy-agent.env
PETBOX_URL=https://petbox.3po.su \
PETBOX_ADMIN_KEY=yb_key_...   # a key with deploy:write \
  ./enroll.sh vdsina-1 "net.x,disk=nvme"

# 3. install + start the service
sudo cp petbox-deploy-agent.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now petbox-deploy-agent
journalctl -u petbox-deploy-agent -f
```

## Local PC as a node (WSL2)

Inside a WSL2 Ubuntu distro with Docker engine and systemd enabled
(`/etc/wsl.conf` → `[boot]\nsystemd=true`), the steps are identical — tag it `--ephemeral`
so failover treats it as a come-and-go node:

```sh
./enroll.sh local-wsl "net.kinopub" --ephemeral
```

## Container contract

Managed containers are named `petbox-<service>` and carry labels `petbox.managed=1`,
`petbox.service`, `petbox.confighash`, `petbox.project`. Env is delivered via
`--env-file` (so secrets don't leak into `ps`). Restart policy is `unless-stopped`;
health/restart is delegated to Docker (`HEALTHCHECK` in the image).
