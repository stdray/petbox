#!/usr/bin/env bash
# Install the vendored pi configuration into ~/.pi/agent.
#
# Copies (never links): pi loads these files at startup and one of them is an
# extension that assembles a system-prompt section, so a stale link would be a
# silent behaviour change. Idempotent — running it twice is a no-op.
#
# Usage: tools/pi/deploy.sh [--dry-run]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
target="${PI_AGENT_DIR:-$HOME/.pi/agent}"
dry=0
[ "${1:-}" = "--dry-run" ] && dry=1

install_file() {
  local src="$1" dst="$2"
  mkdir -p "$(dirname "$dst")"
  if [ -f "$dst" ] && cmp -s "$src" "$dst"; then
    echo "  = ${dst#"$target"/}"
    return
  fi
  if [ "$dry" = 1 ]; then
    echo "  + ${dst#"$target"/} (would write)"
    return
  fi
  cp "$src" "$dst"
  echo "  + ${dst#"$target"/}"
}

echo "pi config -> $target"
for f in "$here"/agents/*.md; do
  install_file "$f" "$target/agents/$(basename "$f")"
done
for f in "$here"/rules/amd-*.md "$here"/rules/pbx-*.md; do
  install_file "$f" "$target/skills/$(basename "$f")"
done
for f in "$here"/extensions/*.ts; do
  install_file "$f" "$target/extensions/$(basename "$f")"
done

if [ "$dry" = 1 ]; then
  echo "dry run: nothing written"
else
  echo "done — restart pi (extensions load at startup; /reload does not pick up new files)"
fi
