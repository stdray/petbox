#!/usr/bin/env bash
# Install the vendored pi configuration into ~/.pi/agent.
#
# Copies (never links): pi loads these files at startup and one of them is an
# extension that assembles a system-prompt section, so a stale link would be a
# silent behaviour change. Idempotent — running it twice is a no-op.
#
# ROLE FILES ARE MERGED, NOT COPIED. `model:` and `thinking:` in a role's frontmatter belong to
# the Roles panel, which writes them into the INSTALLED file (that is where the
# `*.md.bak-before-models-panel-*` litter comes from). A plain copy silently reverted the
# owner's model choice on every deploy, and the repo then looked like the source of truth while
# the panel kept losing. So the installed values win for those two keys, and the repo's own
# values are the baseline a fresh machine gets. Everything else in the file still comes from
# the repo.
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

# Repo role file + installed pins -> stdout. A key the installed file carries replaces the repo's
# value in place; one the repo dropped is spliced back before the closing fence; with no installed
# file (fresh machine) the repo file passes through untouched.
merge_role() {
  local src="$1" dst="$2"
  awk \
    -v model="$([ -f "$dst" ] && grep -m1 '^model:' "$dst" || true)" \
    -v think="$([ -f "$dst" ] && grep -m1 '^thinking:' "$dst" || true)" '
    NR == 1 && $0 == "---" { in_fm = 1; print; next }
    in_fm && $0 == "---" {
      if (!seen_model && model != "") print model
      if (!seen_think && think != "") print think
      in_fm = 0; print; next
    }
    in_fm && /^model:/    { print (model != "" ? model : $0); seen_model = 1; next }
    in_fm && /^thinking:/ { print (think != "" ? think : $0); seen_think = 1; next }
    { print }
  ' "$src"
}

install_role() {
  local src="$1" dst="$2"
  mkdir -p "$(dirname "$dst")"
  local merged
  merged="$(mktemp)"
  merge_role "$src" "$dst" > "$merged"
  if [ -f "$dst" ] && cmp -s "$merged" "$dst"; then
    echo "  = ${dst#"$target"/}"
    rm -f "$merged"
    return
  fi
  if [ "$dry" = 1 ]; then
    echo "  + ${dst#"$target"/} (would write; model:/thinking: kept from the installed file)"
    rm -f "$merged"
    return
  fi
  mv "$merged" "$dst"
  echo "  + ${dst#"$target"/}"
}

echo "pi config -> $target"
for f in "$here"/agents/*.md; do
  install_role "$f" "$target/agents/$(basename "$f")"
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
