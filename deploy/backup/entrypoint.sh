#!/bin/sh
# Sidecar entrypoint: run one backup immediately (so a fresh deploy fails loudly if
# creds/reachability are wrong instead of silently waiting for the first cron tick),
# then hand off to busybox crond for the schedule.
set -eu

CRON="${BACKUP_CRON:-17 */6 * * *}"

echo "[entrypoint] initial backup run"
/usr/local/bin/backup.sh || echo "[entrypoint] WARNING: initial backup failed (see logs above); crond will retry on schedule"

echo "[entrypoint] scheduling: $CRON"
mkdir -p /etc/crontabs
echo "$CRON /usr/local/bin/backup.sh >/proc/1/fd/1 2>/proc/1/fd/2" > /etc/crontabs/root

exec crond -f -l 8
