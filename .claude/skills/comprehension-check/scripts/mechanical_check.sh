#!/usr/bin/env bash
# mechanical_check.sh — measure 2 of the comprehension-check skill: the part of
# "did this cover the card" that needs no model judgment at all.
#
# Checks:
#   1. Do the artifacts a card named exist on disk?
#   2. Does `git diff --stat <base>...HEAD` touch what the card named?
#
# Both are plain facts, not opinions — this script never guesses at intent.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: mechanical_check.sh --artifacts <file> [--base <git-ref>] [--repo <path>]

  --artifacts <file>  Required. One expected path per line (relative to --repo, or
                       absolute). Blank lines and lines starting with # are ignored.
  --base <git-ref>    Base ref to diff against for the touch check (default: origin/main).
                       Pass "-" to skip the git diff check entirely (useful for a static
                       fixture with no repo history, e.g. this skill's own self-test).
  --repo <path>       Repo root to run git in (default: current directory).
  -h, --help          Show this help.

Exit status: 0 only if every named artifact exists AND (diff check skipped, or every
named artifact that looks like a repo-relative path was touched by the diff). Non-zero
otherwise — read the printed report to see which check failed.
EOF
}

artifacts_file=""
base_ref="origin/main"
repo="."

while [ $# -gt 0 ]; do
  case "$1" in
    --artifacts) artifacts_file="$2"; shift 2 ;;
    --base) base_ref="$2"; shift 2 ;;
    --repo) repo="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -z "$artifacts_file" ]; then
  echo "error: --artifacts is required" >&2
  usage >&2
  exit 2
fi
if [ ! -f "$artifacts_file" ]; then
  echo "error: artifacts file not found: $artifacts_file" >&2
  exit 2
fi

mapfile -t artifacts < <(grep -vE '^\s*(#|$)' "$artifacts_file")

overall_ok=0

echo "== Artifacts =="
missing=0
for a in "${artifacts[@]}"; do
  path="$a"
  if [ "${path:0:1}" != "/" ] && [ "${path:1:1}" != ":" ]; then
    path="$repo/$a"
  fi
  if [ -e "$path" ]; then
    echo "OK      $a"
  else
    echo "MISSING $a"
    missing=1
  fi
done
if [ "$missing" -ne 0 ]; then overall_ok=1; fi

echo
if [ "$base_ref" = "-" ]; then
  echo "== Diff overlap == (skipped: --base -)"
else
  echo "== Diff overlap (git diff --stat ${base_ref}...HEAD in $repo) =="
  if ! touched="$(git -C "$repo" diff --stat "${base_ref}...HEAD" 2>&1)"; then
    echo "could not diff against $base_ref — is it fetched? raw error:"
    echo "$touched"
    overall_ok=1
  else
    if [ -z "$touched" ]; then
      echo "(no diff — nothing touched at all)"
      if [ "${#artifacts[@]}" -gt 0 ]; then overall_ok=1; fi
    else
      echo "$touched"
    fi
    echo
    for a in "${artifacts[@]}"; do
      if echo "$touched" | grep -qF -- "$a"; then
        echo "TOUCHED     $a"
      else
        echo "NOT TOUCHED $a"
        overall_ok=1
      fi
    done
  fi
fi

echo
if [ "$overall_ok" -eq 0 ]; then
  echo "RESULT: mechanical check clean"
else
  echo "RESULT: mechanical check found a gap — see MISSING/NOT TOUCHED lines above"
fi
exit "$overall_ok"
