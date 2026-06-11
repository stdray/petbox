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
import shutil
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

# The dedicated Caddy include dir this agent owns for SITE deployments. The host's
# Caddyfile must `import /etc/caddy/petbox.d/*.caddy` once (enroll/self-setup does this;
# see doc/guides/deploy-fleet.md). The agent never touches anything outside this dir.
CADDY_DIR = "/etc/caddy/petbox.d"


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


def runspec_args(spec: dict | None) -> list[str]:
    """Map a deployment's runSpec (compose-subset) to docker run flags. Pure.

    Field order is fixed (ports, volumes, restart, healthcheck, resources, network,
    labels) so the produced command line is deterministic and unit-testable.
    """
    spec = spec or {}
    args: list[str] = []
    for p in spec.get("ports") or []:
        args += ["-p", p]
    for v in spec.get("volumes") or []:
        args += ["-v", v]
    args += ["--restart", spec.get("restart") or "unless-stopped"]
    hc = spec.get("healthcheck") or {}
    if hc.get("cmd"):
        args += ["--health-cmd", hc["cmd"]]
        if hc.get("interval"):
            args += ["--health-interval", hc["interval"]]
        if hc.get("timeout"):
            args += ["--health-timeout", hc["timeout"]]
        if hc.get("retries") is not None:
            args += ["--health-retries", str(hc["retries"])]
    res = spec.get("resources") or {}
    if res.get("memory"):
        args += ["--memory", res["memory"]]
    if res.get("cpus") is not None:
        args += ["--cpus", str(res["cpus"])]
    if spec.get("network"):
        args += ["--network", spec["network"]]
    for k, v in (spec.get("labels") or {}).items():
        args += ["--label", f"{k}={v}"]
    return args


def runspec_command(spec: dict | None) -> list[str]:
    """The CMD override appended after the image (empty = use the image's CMD). Pure."""
    return list((spec or {}).get("command") or [])


# --- site routes (reverse-proxy via Caddy) -----------------------------------

def render_caddy_route(domain: str, port: int) -> str:
    """One site's Caddy block: domain -> loopback port."""
    return f"{domain} {{\n\treverse_proxy 127.0.0.1:{port}\n}}\n"


def plan_caddy(desired: list[dict], current: dict[str, str]) -> tuple[dict[str, str], list[str]]:
    """Decide the file writes/removes that make the Caddy include dir match the desired
    site routes. Pure: current = {filename: content} of the dir's *.caddy files.

    A route exists for every desired-RUNNING deployment whose runSpec carries site{};
    stopped/removed sites lose their file (Caddy stops serving the domain instead of
    proxying into a dead container).
    """
    want: dict[str, str] = {}
    for d in desired:
        if d.get("desired") != DESIRED_RUNNING:
            continue
        site = (d.get("runSpec") or {}).get("site") or {}
        if site.get("domain") and site.get("port"):
            want[f"{d['service']}.caddy"] = render_caddy_route(site["domain"], site["port"])
    writes = {name: content for name, content in want.items() if current.get(name) != content}
    removes = [name for name in current if name not in want]
    return writes, removes


def caddy_available() -> bool:
    return shutil.which("caddy") is not None


def read_caddy_dir() -> dict[str, str]:
    if not os.path.isdir(CADDY_DIR):
        return {}
    result: dict[str, str] = {}
    for name in os.listdir(CADDY_DIR):
        if name.endswith(".caddy"):
            with open(os.path.join(CADDY_DIR, name)) as f:
                result[name] = f.read()
    return result


def sync_caddy(desired: list[dict]) -> None:
    """Reconcile the Caddy include dir to the desired site routes; reload on change."""
    writes, removes = plan_caddy(desired, read_caddy_dir())
    if not writes and not removes:
        return
    os.makedirs(CADDY_DIR, exist_ok=True)
    for name, content in writes.items():
        with open(os.path.join(CADDY_DIR, name), "w") as f:
            f.write(content)
    for name in removes:
        os.remove(os.path.join(CADDY_DIR, name))
    _log(f"caddy: {len(writes)} route(s) written, {len(removes)} removed; reloading")
    subprocess.run(["systemctl", "reload", "caddy"], check=False)


def run_container(item: dict) -> None:
    """(Re)create and start a container for a desired deployment."""
    service = item["service"]
    remove_container(service)  # idempotent recreate
    env = item.get("env") or {}
    spec = item.get("runSpec") or {}
    fd, env_path = tempfile.mkstemp(prefix="petbox-env-")
    try:
        with os.fdopen(fd, "w") as f:
            for k, v in env.items():
                f.write(f"{k}={v}\n")
        _docker([
            "run", "-d",
            "--name", f"petbox-{service}",
            *runspec_args(spec),
            "--env-file", env_path,
            # control labels go AFTER user labels — the last occurrence wins in docker
            "--label", f"{MANAGED_LABEL}=1",
            "--label", f"{SVC_LABEL}={service}",
            "--label", f"{HASH_LABEL}={item['configHash']}",
            "--label", f"{PROJECT_LABEL}={item.get('project', '')}",
            item["imageDigest"],
            *runspec_command(spec),
        ])
    finally:
        os.unlink(env_path)


def plan_site_errors(desired: list[dict], caddy_ok: bool) -> dict[str, str]:
    """Sites assigned to a host without caddy = an explicit per-service error. Pure."""
    if caddy_ok:
        return {}
    return {
        d["service"]: "site route not applied: caddy is not available on this node"
        for d in desired
        if d.get("desired") == DESIRED_RUNNING
        and ((d.get("runSpec") or {}).get("site") or {}).get("domain")
    }


def apply(actions: list[dict]) -> dict[str, str]:
    """Execute the planned actions; returns {service: error} for the failed ones."""
    errors: dict[str, str] = {}
    for a in actions:
        try:
            if a["action"] == "run":
                run_container(a["item"])
            elif a["action"] == "remove":
                remove_container(a["service"])
        except subprocess.CalledProcessError as e:
            detail = (e.stderr or str(e)).strip().splitlines()[-1] if isinstance(e.stderr, str) and e.stderr.strip() else str(e)
            errors[a["service"]] = f"{a['action']} failed: {detail}"
            print(f"[agent] action {a['action']} {a['service']} failed: {detail}", file=sys.stderr)
    return errors


def detect_capabilities() -> list[str]:
    caps = []
    if shutil.which("docker"):
        caps.append("docker")
    if caddy_available():
        caps.append("caddy")
    return caps


def build_heartbeat(actual: dict[str, dict], errors: dict[str, str] | None = None,
                    capabilities: list[str] | None = None) -> dict:
    errors = errors or {}
    reports = [
        {
            "service": svc,
            "containerId": c["container_id"],
            "state": c["state"],
            "imageDigest": c.get("image"),
            # a reconcile error (e.g. site route not applied) makes the service unhealthy
            # even if its container runs — the error must be visible, not averaged away
            "healthy": c["state"] == ACTUAL_RUNNING and svc not in errors,
            "error": errors.get(svc),
        }
        for svc, c in actual.items()
    ]
    # a service that errored before its container exists must still be visible upstream
    for svc, msg in errors.items():
        if svc not in actual:
            reports.append({"service": svc, "containerId": None, "state": ACTUAL_MISSING,
                            "imageDigest": None, "healthy": False, "error": msg})
    hb: dict = {"actual": reports}
    if capabilities is not None:
        hb["capabilities"] = capabilities
    return hb


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


def _log(msg: str) -> None:
    # flush so lines reach journald immediately even without PYTHONUNBUFFERED
    print(f"[agent] {msg}", flush=True)


def reconcile_once(base_url: str, key: str) -> None:
    poll = _request(f"{base_url}/agent/poll", key, "GET", None)
    desired = poll.get("deployments", [])
    actual = list_managed()
    actions = plan_actions(desired, actual)
    errors: dict[str, str] = {}
    if actions:
        _log(f"{len(actions)} action(s): "
             + ", ".join(f"{a['action']}:{a['service']}" for a in actions))
        errors.update(apply(actions))
        actual = list_managed()  # re-read after applying
    caddy_ok = caddy_available()
    if caddy_ok:
        sync_caddy(desired)
    errors.update(plan_site_errors(desired, caddy_ok))
    _request(f"{base_url}/agent/heartbeat", key, "POST",
             build_heartbeat(actual, errors, detect_capabilities()))
    # one line per cycle so the agent is visibly alive in journald (not silent)
    _log(f"reconciled: {len(desired)} desired, {len(actions)} action(s), "
         f"{len(actual)} running, {len(errors)} error(s); heartbeat ok")


def main() -> int:
    base_url = os.environ.get("PETBOX_URL", "").rstrip("/")
    key = os.environ.get("PETBOX_NODE_KEY", "")
    interval = int(os.environ.get("POLL_INTERVAL", "30"))
    if not base_url or not key:
        print("PETBOX_URL and PETBOX_NODE_KEY are required", file=sys.stderr)
        return 2

    _log(f"starting; url={base_url} interval={interval}s")
    while True:
        try:
            reconcile_once(base_url, key)
        except urllib.error.HTTPError as e:
            print(f"[agent] poll/heartbeat HTTP {e.code}: {e.reason}", file=sys.stderr, flush=True)
        except urllib.error.URLError as e:
            print(f"[agent] connection error: {e.reason}", file=sys.stderr, flush=True)
        except Exception as e:  # noqa: BLE001 — agent must never crash the loop
            print(f"[agent] unexpected: {e}", file=sys.stderr, flush=True)
        time.sleep(interval)


if __name__ == "__main__":
    raise SystemExit(main())
