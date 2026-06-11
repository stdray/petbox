#!/usr/bin/env bash
# Onboard this machine as a PetBox deploy node — the ONE command of bare-server setup.
# On a supported OS it brings the host to deploy-readiness itself: installs docker and
# caddy if missing, wires the Caddyfile to the petbox include dir, registers the node
# (minting its node-scoped key), installs and starts the agent's systemd unit.
# Idempotent: re-running re-checks everything and rotates the node key.
#
#   PETBOX_URL=https://petbox.3po.su \
#   PETBOX_ADMIN_KEY=yb_key_...        # a key with deploy:write \
#   ./enroll.sh <node-id> [tags-csv] [--ephemeral]
#
# Example:
#   ./enroll.sh vdsina-1 "net.x,disk=nvme"
#   ./enroll.sh local-wsl "net.kinopub" --ephemeral
#
# Supported OS: Ubuntu LTS (a short FIXED list — deliberately not generic).
# PETBOX_SKIP_OS_CHECK=1 skips the gate (you provision docker/caddy yourself).
set -euo pipefail

: "${PETBOX_URL:?set PETBOX_URL}"
: "${PETBOX_ADMIN_KEY:?set PETBOX_ADMIN_KEY (a deploy:write key)}"

SUPPORTED_UBUNTU=("22.04" "24.04" "26.04")
AGENT_DIR=/opt/petbox-deploy-agent
CADDY_INCLUDE_DIR=/etc/caddy/petbox.d
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# Fail fast if we can't write /etc — otherwise the `sudo tee` below hangs forever on a
# password prompt in a non-interactive run (no TTY). Need root or passwordless sudo.
if [[ "$(id -u)" -ne 0 ]] && ! sudo -n true 2>/dev/null; then
  echo "enroll.sh needs root or passwordless sudo (it installs packages and writes /etc)." >&2
  echo "Re-run as root (sudo -i, or on WSL2: wsl -u root), or configure NOPASSWD sudo." >&2
  exit 1
fi
SUDO=""; [[ "$(id -u)" -ne 0 ]] && SUDO="sudo"

# --- OS gate: the supported list is limited and fixed, anything else is a clear no ----
if [[ "${PETBOX_SKIP_OS_CHECK:-}" != "1" ]]; then
  os_id=""; os_ver=""
  if [[ -r /etc/os-release ]]; then
    . /etc/os-release
    os_id="${ID:-}"; os_ver="${VERSION_ID:-}"
  fi
  supported=false
  for v in "${SUPPORTED_UBUNTU[@]}"; do
    [[ "$os_id" == "ubuntu" && "$os_ver" == "$v" ]] && supported=true
  done
  if [[ "$supported" != true ]]; then
    echo "Unsupported OS: ${os_id:-unknown} ${os_ver:-?}. PetBox node self-setup supports Ubuntu ${SUPPORTED_UBUNTU[*]} only." >&2
    echo "Set PETBOX_SKIP_OS_CHECK=1 to proceed anyway (you provision docker/caddy yourself)." >&2
    exit 1
  fi
fi

NODE_ID="${1:?usage: enroll.sh <node-id> [tags-csv] [--ephemeral]}"
TAGS="${2:-}"
EPHEMERAL=false
[[ "${3:-}" == "--ephemeral" ]] && EPHEMERAL=true

# --- register the node + mint its key (early: fail before any installs if the API says no)
resp=$(curl -fsS -X POST "$PETBOX_URL/api/deploy/nodes" \
  -H "X-Api-Key: $PETBOX_ADMIN_KEY" \
  -H "Content-Type: application/json" \
  -d "{\"id\":\"$NODE_ID\",\"tags\":\"$TAGS\",\"ephemeral\":$EPHEMERAL,\"mintKey\":true}")

KEY=$(printf '%s' "$resp" | python3 -c 'import sys,json; print(json.load(sys.stdin)["key"])')
if [[ -z "$KEY" || "$KEY" == "None" ]]; then
  echo "enroll failed: $resp" >&2
  exit 1
fi

$SUDO tee /etc/petbox-deploy-agent.env >/dev/null <<EOF
PETBOX_URL=$PETBOX_URL
PETBOX_NODE_KEY=$KEY
POLL_INTERVAL=30
EOF
$SUDO chmod 600 /etc/petbox-deploy-agent.env
echo "node '$NODE_ID' registered; wrote /etc/petbox-deploy-agent.env"

# --- docker (official convenience script; skipped when already present) --------------
if ! command -v docker >/dev/null 2>&1; then
  echo "installing docker..."
  curl -fsSL https://get.docker.com | $SUDO sh
else
  echo "docker: present ($(docker --version 2>/dev/null || true))"
fi

# --- caddy (official cloudsmith apt repo; skipped when already present) --------------
if ! command -v caddy >/dev/null 2>&1; then
  echo "installing caddy..."
  $SUDO apt-get install -y debian-keyring debian-archive-keyring apt-transport-https curl >/dev/null
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' \
    | $SUDO gpg --batch --yes --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' \
    | $SUDO tee /etc/apt/sources.list.d/caddy-stable.list >/dev/null
  $SUDO apt-get update -qq
  $SUDO apt-get install -y caddy
else
  echo "caddy: present ($(caddy version 2>/dev/null | head -1 || true))"
fi

# --- wire the petbox include dir into the Caddyfile (the agent owns only that dir) ----
$SUDO mkdir -p "$CADDY_INCLUDE_DIR"
if ! $SUDO grep -qsF "import $CADDY_INCLUDE_DIR/*.caddy" /etc/caddy/Caddyfile 2>/dev/null; then
  echo "import $CADDY_INCLUDE_DIR/*.caddy" | $SUDO tee -a /etc/caddy/Caddyfile >/dev/null
  $SUDO systemctl reload caddy 2>/dev/null || true
  echo "Caddyfile: import line added"
fi

# --- install + start the agent (systemd) ----------------------------------------------
if [[ ! -f "$SCRIPT_DIR/petbox_deploy_agent.py" ]]; then
  echo "warning: petbox_deploy_agent.py not found next to enroll.sh — copy it to $AGENT_DIR and start the unit yourself." >&2
  exit 0
fi
$SUDO mkdir -p "$AGENT_DIR"
$SUDO cp "$SCRIPT_DIR/petbox_deploy_agent.py" "$AGENT_DIR/"
if [[ -d /run/systemd/system ]]; then
  $SUDO cp "$SCRIPT_DIR/petbox-deploy-agent.service" /etc/systemd/system/
  $SUDO systemctl daemon-reload
  $SUDO systemctl enable --now petbox-deploy-agent
  $SUDO systemctl restart petbox-deploy-agent   # pick up a rotated key on re-enroll
  echo "agent: enabled + running (journalctl -u petbox-deploy-agent -f)"
else
  echo "no systemd here — run the agent in the foreground:" >&2
  echo "  PETBOX_URL=$PETBOX_URL PETBOX_NODE_KEY=<see /etc/petbox-deploy-agent.env> python3 $AGENT_DIR/petbox_deploy_agent.py" >&2
fi

echo "node '$NODE_ID' enrolled — it should appear online within ~30s (deploy.node_list or /ui/admin/sys/deploy)"
