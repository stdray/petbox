#!/bin/sh
# Offsite backup of the PetBox data volume via restic.
#   compact (data minus logs) -> R2          — small, keeps the R2 free tier alive
#   full    (data incl. logs)  -> FirstVDS S3 — the fat copy
# Source is BackupService's newest *-auto snapshot dir (already a consistent set of
# VACUUM-INTO copies — no live WAL touched). Both repos also carry data/keys/
# (the DataProtection key ring), which the .db-only snapshot does NOT contain and
# which is required to decrypt config secrets on restore. PETBOX_MASTER_KEY itself
# lives in the deploy secrets (GH/KeePass), not in the backup.
#
# Both repos share RESTIC_PASSWORD (read from env by restic). Retention + prune +
# an integrity check run after each push.
#
# Each leg is independent: one leg's failure must not stop the other leg from
# running (see run_leg below). The script exits 1 if either leg failed (so cron
# logs + entrypoint reflect failure) and 0 only if both legs succeeded.
#
# Optional, env-gated extras (all no-ops if their env vars are unset):
#   - Telegram alert on failure (anti-spammed via /state/alert-status) + a single
#     "recovered" message when a subsequent run goes back to ok.
#   - A success heartbeat ping to HEALTHCHECK_URL (dead-man's-switch style).
set -u

DATA_DIR="${DATA_DIR:-/data}"
KEEP_DAILY="${RESTIC_KEEP_DAILY:-7}"
KEEP_WEEKLY="${RESTIC_KEEP_WEEKLY:-4}"
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"

STATE_DIR="${STATE_DIR:-/state}"
ALERT_REPEAT_HOURS="${ALERT_REPEAT_HOURS:-24}"
TELEGRAM_BOT_TOKEN="${TELEGRAM_BOT_TOKEN:-}"
TELEGRAM_CHAT_ID="${TELEGRAM_CHAT_ID:-}"
HEALTHCHECK_URL="${HEALTHCHECK_URL:-}"

log() { echo "[backup $(date -u +%FT%TZ)] $*"; }

newest="$(ls -1d "$DATA_DIR"/backups/*-auto/ 2>/dev/null | sort | tail -1 || true)"
if [ -z "$newest" ]; then
	log "ERROR: no *-auto snapshot under $DATA_DIR/backups — is the data volume mounted?"
	exit 1
fi
newest="${newest%/}"
log "source snapshot: $newest"

# push REPO TAG EXTRA_ARGS ACCESS_KEY SECRET_KEY
push() {
	repo="$1"; tag="$2"; extra="$3"
	export AWS_ACCESS_KEY_ID="$4"
	export AWS_SECRET_ACCESS_KEY="$5"
	restic -r "$repo" snapshots >/dev/null 2>&1 || { log "init $tag repo"; restic -r "$repo" init; }
	log "backup $tag -> $repo"
	# shellcheck disable=SC2086 — $extra is an intentional word-split of restic flags
	restic -r "$repo" backup "$newest" "$DATA_DIR/keys" --tag "$tag" --host petbox $extra
	restic -r "$repo" forget --tag "$tag" --keep-daily "$KEEP_DAILY" --keep-weekly "$KEEP_WEEKLY" --prune
	restic -r "$repo" check
	log "$tag ok"
}

# run_leg NAME LOGFILE PUSH_ARGS... — runs push with its own `set -e` (so the first
# failing restic call aborts just this leg, same as before) inside a subshell whose
# output is captured to LOGFILE. NOTE: the subshell's exit status is captured via a
# plain `$?` on its own line, deliberately NOT as the direct operand of `if`/`&&`/
# `||` — busybox ash (unlike bash) silently ignores an inner `set -e` when the
# subshell itself is the direct condition of if/while, so `if (set -e; push …); then`
# would never abort a leg early on its first failing restic call. Capturing $?
# afterwards avoids that pitfall. Output is replayed to stdout so cron/docker logs
# still show it. Returns push's exit status.
run_leg() {
	_name="$1"; _log="$2"; shift 2
	(set -e; push "$@") >"$_log" 2>&1
	_status=$?
	cat "$_log"
	if [ "$_status" -eq 0 ]; then
		log "$_name ok"
	else
		log "$_name FAILED"
	fi
	return "$_status"
}

# NOTE: each run_leg call is deliberately a standalone statement whose status is
# captured via a following `$?` assignment, NOT `run_leg ... || compact_ok=1`.
# busybox ash suppresses errexit for a command's ENTIRE subtree — including any
# explicit `set -e` in subshells nested arbitrarily deep inside it (see run_leg's
# internal subshell above) — whenever that command is the direct operand of `||`
# (same rule that bit the `if` form). Using `||` here would silently defeat
# run_leg's internal `set -e` and make failed legs read as successful again.

# ── compact -> R2 (exclude the heavy, least-critical logs subtree) ──
run_leg compact /tmp/backup-compact.log \
	"s3:${R2_S3_ENDPOINT}/${R2_BUCKET}/compact" compact "--exclude $newest/logs" \
	"$R2_ACCESS_KEY_ID" "$R2_SECRET_ACCESS_KEY"
compact_ok=$?

# ── full -> FirstVDS S3 (everything, incl. logs) ──
run_leg full /tmp/backup-full.log \
	"s3:${FVDS_S3_ENDPOINT}/${FVDS_BUCKET}/full" full "" \
	"$FVDS_ACCESS_KEY_ID" "$FVDS_SECRET_ACCESS_KEY"
full_ok=$?

if [ "$compact_ok" -eq 0 ] && [ "$full_ok" -eq 0 ]; then
	overall="ok"
else
	overall="fail"
fi
log "all backups done ($overall)"

# ── optional: anti-spam Telegram alert on failure / recovery ──
# Only active when both TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID are set; otherwise
# every step below is a silent no-op.
tg_send() {
	msg="$1"
	[ -n "$TELEGRAM_BOT_TOKEN" ] && [ -n "$TELEGRAM_CHAT_ID" ] || return 0
	curl -fsS -m 15 --data-urlencode "chat_id=$TELEGRAM_CHAT_ID" --data-urlencode "text=$msg" \
		"https://api.telegram.org/bot$TELEGRAM_BOT_TOKEN/sendMessage" >/dev/null 2>&1 \
		|| log "WARNING: telegram alert failed to send"
}

if [ -n "$TELEGRAM_BOT_TOKEN" ] && [ -n "$TELEGRAM_CHAT_ID" ]; then
	state_file="$STATE_DIR/alert-status"
	prev_status="unknown"
	prev_alert_epoch=0
	if [ -f "$state_file" ]; then
		read -r prev_status prev_alert_epoch < "$state_file" 2>/dev/null || true
	fi
	case "$prev_alert_epoch" in ''|*[!0-9]*) prev_alert_epoch=0 ;; esac
	now_epoch="$(date +%s)"
	host="$(hostname 2>/dev/null || echo unknown)"

	new_status="$overall"
	new_epoch="$prev_alert_epoch"

	if [ "$overall" = "fail" ]; then
		failed_legs=""
		[ "$compact_ok" -eq 0 ] || failed_legs="${failed_legs}compact "
		[ "$full_ok" -eq 0 ] || failed_legs="${failed_legs}full "

		elapsed=$(( now_epoch - prev_alert_epoch ))
		if [ "$prev_status" != "fail" ] || [ "$elapsed" -ge $(( ALERT_REPEAT_HOURS * 3600 )) ]; then
			excerpt=""
			[ "$compact_ok" -eq 0 ] || excerpt="${excerpt}--- compact ---
$(tail -n 15 /tmp/backup-compact.log 2>/dev/null)
"
			[ "$full_ok" -eq 0 ] || excerpt="${excerpt}--- full ---
$(tail -n 15 /tmp/backup-full.log 2>/dev/null)
"
			msg="🔴 petbox backup FAILED on $host
failed leg(s): ${failed_legs% }

$excerpt"
			msg="$(printf '%s' "$msg" | cut -c1-3500)"
			tg_send "$msg"
			new_epoch="$now_epoch"
		fi
	else
		if [ "$prev_status" = "fail" ]; then
			tg_send "✅ petbox backup recovered on $host"
		fi
		new_epoch=0
	fi

	mkdir -p "$STATE_DIR" 2>/dev/null || true
	if [ -d "$STATE_DIR" ]; then
		printf '%s %s\n' "$new_status" "$new_epoch" > "$state_file" 2>/dev/null \
			|| log "WARNING: could not write state file $state_file"
	else
		log "WARNING: $STATE_DIR not writable — alert state not persisted"
	fi
fi

# ── optional: success heartbeat (dead-man's-switch) ──
if [ "$overall" = "ok" ] && [ -n "$HEALTHCHECK_URL" ]; then
	curl -fsS -m 10 "$HEALTHCHECK_URL" >/dev/null 2>&1 || log "WARNING: healthcheck ping failed"
fi

[ "$overall" = "ok" ] || exit 1
exit 0
