#!/bin/sh
# Sidecar entrypoint. Two responsibilities, both learned the hard way on 2026-08-28
# (see work/backup-deploy-kills-restic-stale-lock).
#
# 1) PROVE THE REPOS ARE REACHABLE AT DEPLOY TIME — CHEAPLY.
#    This used to run a full `backup.sh` on every container start, so a fresh deploy with
#    broken credentials failed loudly instead of waiting up to 6 h for the first cron tick.
#    That intent is worth keeping; the full push was not. On 2026-08-28 three stack
#    re-creations inside six minutes meant three full pushes (~240 MiB read / ~130 MiB
#    written to R2 apiece), and the deploy that landed mid-push killed restic with its
#    repository lock still held — R2 retention, prune and check then failed for hours.
#    `restic cat config` fetches and decrypts one small object: it proves the endpoint,
#    the S3 credentials AND RESTIC_PASSWORD in a single call, takes no repository lock and
#    writes nothing. A failure stays a WARNING and never a non-zero exit — under
#    `restart: unless-stopped` a fatal probe would turn a transient S3 outage into a crash
#    loop, and the scheduled runs (with their Telegram alert) are the real signal.
#    The probe runs AFTER crond is already up, and under `timeout`, for one measured
#    reason: against an unreachable or not-yet-created bucket restic retries its backend
#    with a long backoff and `cat config` sat there for OVER TEN MINUTES. Probing before
#    scheduling — or unbounded — would mean an S3 hiccup at deploy time leaves the sidecar
#    with no cron schedule at all for as long as it lasts. Diagnostics must never be able
#    to delay the thing they are diagnosing.
#
# 2) BE A PID 1 THAT FORWARDS SIGTERM.
#    Measured on restic/restic:0.18.1: busybox crond as PID 1 IGNORES SIGTERM — `docker
#    stop` waits out the whole grace period and ends in SIGKILL (exit 137), idle or busy,
#    and crond relays nothing to the job it spawned. So the restic underneath never saw a
#    shutdown signal at all; it was killed outright, lock and all. Hence crond runs as a
#    CHILD here and this shell relays SIGTERM into the container, giving restic (which
#    drops its own lock on a signal) and backup.sh's trap the chance to release the lock.
#    Without this relay, the `stop_grace_period` on petbox-backup in deploy/compose.yaml
#    would be pure deploy latency: it would postpone the identical SIGKILL, not prevent it.
set -eu

CRON="${BACKUP_CRON:-17 */6 * * *}"

# How long the shutdown relay waits for a running backup to unwind. Must stay comfortably
# BELOW petbox-backup's stop_grace_period in deploy/compose.yaml — docker must never be the
# one that cuts us off, or we are back to a SIGKILL with the lock held. The two live in
# different files and neither can read the other, so ComposeStopGraceTests asserts the gap.
SHUTDOWN_WAIT_SECONDS=100

# Hard ceiling on each reachability probe — see the header. Long enough that a healthy repo
# always answers, short enough that two dead repos cost the start-up a bounded two minutes
# of log noise and nothing else.
PROBE_TIMEOUT_SECONDS=60

term_handler() {
	trap '' TERM INT
	echo "[entrypoint] SIGTERM: relaying into the container so a running restic can release its lock"
	# A broadcast, not a recorded pid: ash runs a trap only AFTER the current foreground
	# command returns, so signalling backup.sh alone would queue its trap behind a restic
	# that was never told to stop. `kill -TERM -1` reaches restic itself and backup.sh;
	# the kernel excludes PID 1 (this shell) from the broadcast.
	kill -TERM -1 2>/dev/null || true
	waited=0
	while [ "$waited" -lt "$SHUTDOWN_WAIT_SECONDS" ] && pgrep -f /usr/local/bin/backup.sh >/dev/null 2>&1; do
		sleep 1
		waited=$((waited + 1))
	done
	if [ "$waited" -ge "$SHUTDOWN_WAIT_SECONDS" ]; then
		echo "[entrypoint] WARNING: backup still running after ${waited}s — exiting anyway, docker SIGKILLs the rest"
	else
		echo "[entrypoint] backup tree unwound after ${waited}s; exiting"
	fi
	exit 0
}
trap term_handler TERM INT

# check_repo LABEL REPO ACCESS_KEY SECRET_KEY — read-only reachability probe, no lock, no writes.
check_repo() {
	_label="$1"; _repo="$2"
	AWS_ACCESS_KEY_ID="$3"
	AWS_SECRET_ACCESS_KEY="$4"
	export AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY
	if timeout "$PROBE_TIMEOUT_SECONDS" restic -r "$_repo" cat config >/dev/null 2>&1; then
		echo "[entrypoint] $_label repo reachable"
	else
		echo "[entrypoint] WARNING: $_label repo NOT readable within ${PROBE_TIMEOUT_SECONDS}s — bad credentials, bad endpoint, wrong RESTIC_PASSWORD, unreachable S3, or not initialised yet (backup.sh initialises on its first run). Scheduled runs will retry: $CRON"
	fi
}

export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"

# The schedule goes up FIRST — before any network probe — so nothing that talks to S3 can
# delay it. crond runs as a child, NOT exec'd: see (2) in the header.
echo "[entrypoint] scheduling: $CRON"
mkdir -p /etc/crontabs
echo "$CRON /usr/local/bin/backup.sh >/proc/1/fd/1 2>/proc/1/fd/2" > /etc/crontabs/root
crond -f -l 8 &
crond_pid=$!

echo "[entrypoint] checking offsite repo reachability (read-only, nothing is written)"
check_repo compact "s3:${R2_S3_ENDPOINT}/${R2_BUCKET}/compact" "$R2_ACCESS_KEY_ID" "$R2_SECRET_ACCESS_KEY"
check_repo full "s3:${FVDS_S3_ENDPOINT}/${FVDS_BUCKET}/full" "$FVDS_ACCESS_KEY_ID" "$FVDS_SECRET_ACCESS_KEY"

# `wait` returns as soon as a trapped signal arrives, which is what lets term_handler run.
set +e
wait "$crond_pid"
crond_status=$?
set -e
echo "[entrypoint] crond exited with status $crond_status"
exit "$crond_status"
