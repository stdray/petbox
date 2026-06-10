#!/usr/bin/env python3
"""PetBox deploy node-agent.

A thin pull-based reconciler: it polls PetBox for the deployments assigned to this
node, drives Docker to match (start / stop / recreate), and reports actual state back
via heartbeat. Outbound HTTPS only — no inbound port, NAT/firewall friendly.

Zero third-party deps (stdlib urllib + subprocess), so a systemd unit on a plain
Ubuntu box costs ~nothing. Config comes from the environment:

  PETBOX_URL        base URL, e.g. https://petbox.3po.su
  PETBOX_NODE_KEY   the node-scoped API key (agent:poll, agent:heartbeat, logs:ingest)
  POLL_INTERVAL     seconds between reconcile passes (default 30)

The reconcile decision (plan_actions) is a pure function over (desired, actual) so it
is unit-tested without Docker; the Docker calls live in the executor below.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

# --- actual-state model -----------------------------------------------------

# Desired.DesiredState ints (mirror PetBox.Deploy.Data.DesiredState)
DESIRED_STOPPED = 0
DESIRED_RUNNING = 1

# ActualState ints (mirror PetBox.Deploy.Data.ActualState)
ACTUAL_MISSING = 0
ACTUAL_STOPPED = 1
ACTUAL_RUNNING = 2

MANAGED_LABEL = "petbox.managed"
SVC_LABEL = "petbox.service"
HASH_LABEL = "petbox.confighash"
PROJECT_LABEL = "petbox.project"


# --- pure reconcile decision (unit-tested) ----------------------------------

def plan_actions(desired: list[dict], actual: dict[str, dict]) -> list[dict]:
    """Decide the minimal set of actions to make `actual` match `desired`.

    desired: list of poll items {service, imageDigest, desired, configHash, env, ...}
    actual:  {service -> {container_id, state, confighash, ...}} for petbox-managed containers
    returns: list of {action: 'run'|'remove', service, item?} in a stable order.

    - desired Running: run if absent, wrong confighash, or not running; else noop.
    - desired Stopped: remove if present; else noop.
    - self-fence: any managed container whose service is not desired here is removed
      (this node no longer owns it — e.g. after a failover relocation).
    """
    actions: list[dict] = []
    desired_services = {d["service"] for d in desired}

    for d in desired:
        svc = d["service"]
        cur = actual.get(svc)
        if d["desired"] == DESIRED_RUNNING:
            if cur is None:
                actions.append({"action": "run", "service": svc, "item": d})
            elif cur.get("confighash") != d["configHash"] or cur.get("state") != ACTUAL_RUNNING:
                actions.append({"action": "run", "service": svc, "item": d})
            # else: running with the right confighash → noop
        else:  # DESIRED_STOPPED
            if cur is not None:
                actions.append({"action": "remove", "service": svc, "item": None})

    # self-fence: managed containers no longer assigned to this node
    for svc in actual:
        if svc not in desired_services:
            actions.append({"action": "remove", "service": svc, "item": None})

    return actions


# --- docker executor --------------------------------------------------------

def _docker(args: list[str], check: bool = True, capture: bool = True) -> str:
    res = subprocess.run(["docker", *args], check=check,
                         capture_output=capture, text=True)
    return (res.stdout or "").strip()


def list_managed() -> dict[str, dict]:
    """Map service -> actual container info for petbox-managed containers."""
    fmt = "{{.ID}}\t{{.State}}\t{{.Label \"%s\"}}\t{{.Label \"%s\"}}\t{{.Image}}" % (SVC_LABEL, HASH_LABEL)
    out = _docker(["ps", "-a", "--filter", f"label={MANAGED_LABEL}=1", "--format", fmt], check=False)
    result: dict[str, dict] = {}
    for line in filter(None, out.splitlines()):
        cid, state, svc, chash, image = (line.split("\t") + ["", "", "", "", ""])[:5]
        if not svc:
            continue
        result[svc] = {
            "container_id": cid,
            "state": ACTUAL_RUNNING if state == "running" else ACTUAL_STOPPED,
            "confighash": chash,
            "image": image,
        }
    return result


def remove_container(service: str) -> None:
    name = f"petbox-{service}"
    _docker(["rm", "-f", name], check=False)


def run_container(item: dict) -> None:
    """(Re)create and start a container for a desired deployment."""
    service = item["service"]
    remove_container(service)  # idempotent recreate
    env = item.get("env") or {}
    fd, env_path = tempfile.mkstemp(prefix="petbox-env-")
    try:
        with os.fdopen(fd, "w") as f:
            for k, v in env.items():
                f.write(f"{k}={v}\n")
        _docker([
            "run", "-d",
            "--name", f"petbox-{service}",
            "--restart", "unless-stopped",
            "--env-file", env_path,
            "--label", f"{MANAGED_LABEL}=1",
            "--label", f"{SVC_LABEL}={service}",
            "--label", f"{HASH_LABEL}={item['configHash']}",
            "--label", f"{PROJECT_LABEL}={item.get('project', '')}",
            item["imageDigest"],
        ])
    finally:
        os.unlink(env_path)


def apply(actions: list[dict]) -> None:
    for a in actions:
        try:
            if a["action"] == "run":
                run_container(a["item"])
            elif a["action"] == "remove":
                remove_container(a["service"])
        except subprocess.CalledProcessError as e:
            print(f"[agent] action {a['action']} {a['service']} failed: {e}", file=sys.stderr)


def build_heartbeat(actual: dict[str, dict]) -> dict:
    return {"actual": [
        {
            "service": svc,
            "containerId": c["container_id"],
            "state": c["state"],
            "imageDigest": c.get("image"),
            "healthy": c["state"] == ACTUAL_RUNNING,
        }
        for svc, c in actual.items()
    ]}


# --- transport --------------------------------------------------------------

def _request(url: str, key: str, method: str, body: dict | None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("X-Api-Key", key)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=30) as resp:
        raw = resp.read()
        return json.loads(raw) if raw else {}


def reconcile_once(base_url: str, key: str) -> None:
    poll = _request(f"{base_url}/agent/poll", key, "GET", None)
    desired = poll.get("deployments", [])
    actual = list_managed()
    actions = plan_actions(desired, actual)
    if actions:
        print(f"[agent] {len(actions)} action(s): "
              + ", ".join(f"{a['action']}:{a['service']}" for a in actions))
        apply(actions)
        actual = list_managed()  # re-read after applying
    _request(f"{base_url}/agent/heartbeat", key, "POST", build_heartbeat(actual))


def main() -> int:
    base_url = os.environ.get("PETBOX_URL", "").rstrip("/")
    key = os.environ.get("PETBOX_NODE_KEY", "")
    interval = int(os.environ.get("POLL_INTERVAL", "30"))
    if not base_url or not key:
        print("PETBOX_URL and PETBOX_NODE_KEY are required", file=sys.stderr)
        return 2

    print(f"[agent] starting; url={base_url} interval={interval}s")
    while True:
        try:
            reconcile_once(base_url, key)
        except urllib.error.HTTPError as e:
            print(f"[agent] poll/heartbeat HTTP {e.code}: {e.reason}", file=sys.stderr)
        except urllib.error.URLError as e:
            print(f"[agent] connection error: {e.reason}", file=sys.stderr)
        except Exception as e:  # noqa: BLE001 — agent must never crash the loop
            print(f"[agent] unexpected: {e}", file=sys.stderr)
        time.sleep(interval)


if __name__ == "__main__":
    raise SystemExit(main())
