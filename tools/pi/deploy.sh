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

# Render `@PETBOX_WIRE_ROOT@` into an extension before installing it.
#
# WHY: the petbox-pi.ts imports are machine-absolute (the user-scoped and project-scoped extension
# dirs sit at different depths, so no relative specifier reaches ~/.petbox/wire from both), and a
# committed machine path is a Linux blocker — on any other home the extension silently fails to
# load. The tracked file carries a placeholder; the install resolves it here from
# ${PETBOX_WIRE_ROOT:-$HOME/.petbox/wire} with forward slashes on every platform, so the same
# source installs anywhere and the second run still writes nothing (the render is deterministic).
# The wire root as a specifier fragment: POSIX form on Linux/macOS, forward-slash Windows form
# (C:/Users/...) under Git Bash — which is what the imports used before the placeholder existed.
wire_root() {
  local root="${PETBOX_WIRE_ROOT:-}"
  if [ -z "$root" ]; then
    root="$HOME/.petbox/wire"
    case "$(uname -s)" in
      MINGW*|MSYS*|CYGWIN*) command -v cygpath >/dev/null 2>&1 && root="$(cygpath -m "$root")" ;;
    esac
  fi
  printf '%s' "${root//\\//}"
}

install_rendered() {
  local src="$1" dst="$2"
  local root
  root="$(wire_root)"
  mkdir -p "$(dirname "$dst")"
  local rendered
  rendered="$(mktemp)"
  # `|` as the sed delimiter: wire roots contain slashes, never pipes.
  sed "s|@PETBOX_WIRE_ROOT@|$root|g" "$src" > "$rendered"
  if [ -f "$dst" ] && cmp -s "$rendered" "$dst"; then
    echo "  = ${dst#"$target"/}"
    rm -f "$rendered"
    return
  fi
  if [ "$dry" = 1 ]; then
    echo "  + ${dst#"$target"/} (would write; wire root -> $root)"
    rm -f "$rendered"
    return
  fi
  mv "$rendered" "$dst"
  echo "  + ${dst#"$target"/} (wire root -> $root)"
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

# A STALE SOURCE TREE REVERTS EVERYTHING. This script copies whatever the calling checkout holds, so
# running it from a checkout parked 48 commits behind main silently rolled the whole rule set back
# to that state — and a deploy looks identical whether it installed the current rules or an old
# copy of them. So the header names the commit it is about to install, and a tree behind the local
# origin/main ref says so loudly. (Local ref, no network: fetch first, or the count is stale too.)
source_sha="$(git -C "$here" rev-parse --short HEAD 2>/dev/null || echo "not-a-git-tree")"
behind="$(git -C "$here" rev-list --count HEAD..origin/main 2>/dev/null || echo 0)"

echo "pi config -> $target  (source $source_sha)"
if [ "$behind" != 0 ]; then
  echo "  !! source tree is $behind commit(s) behind origin/main — this install is NOT the current" \
       "rule set; deploy from a worktree or from an up-to-date main"
fi
for f in "$here"/agents/*.md; do
  install_role "$f" "$target/agents/$(basename "$f")"
done
for f in "$here"/rules/amd-*.md "$here"/rules/pbx-*.md; do
  install_file "$f" "$target/skills/$(basename "$f")"
done

# Skills are RENDERED, not copied: the three PetBox procedures come from the kit's own templates
# (src/clients-ts/petbox-wire/src/templates) through the kit's own renderer, with the pi-specific
# differences applied as anchored deltas — skills.mjs's header explains why a hand copy here would
# drift. It owns its own `=`/`+` output and honours the same dry-run flag and target.
#
# A missing bun is a hard stop, not a skipped step: skills.mjs imports the kit's TypeScript
# renderer, and an install that reports "done" while three skills are absent is exactly the silent
# degradation this step exists to prevent.
if ! command -v bun >/dev/null 2>&1; then
  echo "  !! bun not found — skills NOT installed (skills.mjs imports the kit's renderer)" >&2
  exit 1
fi
if [ "$dry" = 1 ]; then
  bun "$here/skills.mjs" --target "$target" --dry-run
else
  bun "$here/skills.mjs" --target "$target"
fi

# Directory extensions (`extensions/<name>/index.ts`) are mirrored, not copied file by file: pi loads
# `index.ts` from the subdirectory and the sibling modules it imports have to arrive with it. `diff -r`
# is what keeps the second run a no-op, and the `rm -rf` before `cp -r` is what stops a file deleted in
# the repo from surviving in the installed copy forever.
install_dir() {
  local src="$1" dst="$2"
  if [ -d "$dst" ] && diff -r -q "$src" "$dst" >/dev/null 2>&1; then
    echo "  = ${dst#"$target"/}/"
    return
  fi
  if [ "$dry" = 1 ]; then
    echo "  + ${dst#"$target"/}/ (would write)"
    return
  fi
  rm -rf "$dst"
  mkdir -p "$(dirname "$dst")"
  cp -r "$src" "$dst"
  echo "  + ${dst#"$target"/}/"
}

for f in "$here"/extensions/*.ts; do
  [ -e "$f" ] || continue
  if grep -q "@PETBOX_WIRE_ROOT@" "$f"; then
    install_rendered "$f" "$target/extensions/$(basename "$f")"
  else
    install_file "$f" "$target/extensions/$(basename "$f")"
  fi
done
for d in "$here"/extensions/*/; do
  [ -d "$d" ] || continue
  install_dir "${d%/}" "$target/extensions/$(basename "$d")"
done

if [ "$dry" = 1 ]; then
  echo "dry run: nothing written"
else
  echo "done — restart pi (extensions load at startup; /reload does not pick up new files)"
fi
