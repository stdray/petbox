#!/usr/bin/env bash
# Onboard this machine as a PetBox deploy node: register it, mint a node-scoped key,
# and write the agent env file. Run once per node (re-running rotates the key).
#
#   PETBOX_URL=https://petbox.3po.su \
#   PETBOX_ADMIN_KEY=yb_key_...        # a key with deploy:write \
#   ./enroll.sh <node-id> [tags-csv] [--ephemeral]
#
# Example:
#   ./enroll.sh vdsina-1 "net.x,disk=nvme"
#   ./enroll.sh local-wsl "net.kinopub" --ephemeral
set -euo pipefail

: "${PETBOX_URL:?set PETBOX_URL}"
: "${PETBOX_ADMIN_KEY:?set PETBOX_ADMIN_KEY (a deploy:write key)}"

# Fail fast if we can't write /etc — otherwise the `sudo tee` below hangs forever on a
# password prompt in a non-interactive run (no TTY). Need root or passwordless sudo.
if [[ "$(id -u)" -ne 0 ]] && ! sudo -n true 2>/dev/null; then
  echo "enroll.sh needs root or passwordless sudo (it writes /etc/petbox-deploy-agent.env)." >&2
  echo "Re-run as root (sudo -i, or on WSL2: wsl -u root), or configure NOPASSWD sudo." >&2
  exit 1
fi

NODE_ID="${1:?usage: enroll.sh <node-id> [tags-csv] [--ephemeral]}"
TAGS="${2:-}"
EPHEMERAL=false
[[ "${3:-}" == "--ephemeral" ]] && EPHEMERAL=true

resp=$(curl -fsS -X POST "$PETBOX_URL/api/deploy/nodes" \
  -H "X-Api-Key: $PETBOX_ADMIN_KEY" \
  -H "Content-Type: application/json" \
  -d "{\"id\":\"$NODE_ID\",\"tags\":\"$TAGS\",\"ephemeral\":$EPHEMERAL,\"mintKey\":true}")

KEY=$(printf '%s' "$resp" | python3 -c 'import sys,json; print(json.load(sys.stdin)["key"])')
if [[ -z "$KEY" || "$KEY" == "None" ]]; then
  echo "enroll failed: $resp" >&2
  exit 1
fi

sudo tee /etc/petbox-deploy-agent.env >/dev/null <<EOF
PETBOX_URL=$PETBOX_URL
PETBOX_NODE_KEY=$KEY
POLL_INTERVAL=30
EOF
sudo chmod 600 /etc/petbox-deploy-agent.env

echo "node '$NODE_ID' enrolled; wrote /etc/petbox-deploy-agent.env"
echo "next: sudo systemctl enable --now petbox-deploy-agent"
