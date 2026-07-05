#!/usr/bin/env bash
# Push the offsite-backup secrets to GitHub Actions from a LOCAL env file.
# The env file holds real secret values and is never committed (see .gitignore).
#
# Usage:
#   cp deploy/backup/secrets.local.env.example deploy/backup/secrets.local.env
#   # fill in the values (restic password from KeePass, R2 + FirstVDS keys)
#   scripts/set-gh-secrets.sh [ENV_FILE] [--repo owner/name]
#
# Only the known backup keys are pushed; other lines are ignored. gh reads each
# value from stdin, so nothing is echoed and no value ever sits in argv.
set -euo pipefail

ENV_FILE="${1:-deploy/backup/secrets.local.env}"
REPO_ARG=()
if [ "${2:-}" = "--repo" ]; then REPO_ARG=(--repo "${3:?repo required after --repo}"); fi

[ -f "$ENV_FILE" ] || { echo "env file not found: $ENV_FILE"; echo "copy deploy/backup/secrets.local.env.example and fill it"; exit 1; }
command -v gh >/dev/null || { echo "gh CLI not found — install https://cli.github.com/"; exit 1; }

KEYS=" RESTIC_PASSWORD R2_ACCESS_KEY_ID R2_SECRET_ACCESS_KEY R2_S3_ENDPOINT R2_BUCKET FVDS_ACCESS_KEY_ID FVDS_SECRET_ACCESS_KEY FVDS_S3_ENDPOINT FVDS_BUCKET TELEGRAM_BOT_TOKEN TELEGRAM_CHAT_ID HEALTHCHECK_URL "

while IFS='=' read -r key val; do
	case "$key" in ''|\#*) continue ;; esac
	case "$KEYS" in *" $key "*) : ;; *) continue ;; esac
	if [ -z "$val" ]; then echo "skip $key (empty)"; continue; fi
	printf '%s' "$val" | gh secret set "$key" "${REPO_ARG[@]}"
	echo "set  $key"
done < "$ENV_FILE"

echo "done"
