#!/bin/sh
# Offsite backup of the PetBox data volume via restic. Two repos, same contents —
# data only, never logs:
#   compact -> R2          — small, keeps the R2 free tier alive
#   full    -> FirstVDS S3 — the second, independent copy
#
# LOGS ARE NOT BACKED UP. Logs are telemetry, not data: backups restore business
# state, log/metric history is expendable. Owner decision 2026-07-11 — self-logs were
# 79% of every set (7.3 GB offsite vs 635 MB of live data). BackupService already
# stops snapshotting data/logs/** into the set (Backup.ExcludedLogsDirName), and
# EXCLUDE_LOGS below keeps the restic side honest for sets written before that
# change (they still carry logs/ until the 14-set local rotation flushes them, ~7d).
# In the repos, the old fat snapshots age out via `forget --keep-daily/--keep-weekly`
# + `--prune` (up to ~4 weeks for the weeklies).
#
# Source is BackupService's newest *-auto snapshot dir (already a consistent set of
# VACUUM-INTO copies — no live WAL touched). Both repos also carry data/keys/
# (the DataProtection key ring), which the .db-only snapshot does NOT contain and
# which is required to decrypt config secrets on restore. PETBOX_MASTER_KEY itself
# lives in the deploy secrets (GH/KeePass), not in the backup.
#
# Both repos share RESTIC_PASSWORD (read from env by restic). Retention + prune +
# an integrity check run after each push.
#
# LOCKING (work/backup-deploy-kills-restic-stale-lock, 2026-08-28). `forget --prune` and
# `check` need an EXCLUSIVE repository lock, and on 2026-08-28 a deploy SIGKILLed a running
# restic and left its lock behind: every later run pushed its snapshot fine (backup only
# takes a shared lock) and then failed retention, prune and check for hours. Three defences,
# and only the three together work:
#   --retry-lock  a conflicting lock is waited out instead of failing the leg outright
#                 (restic 0.18.1 default is 0s — the incident log literally read
#                 "waiting up to 0s for the lock").
#   unlock        sweeps locks restic considers STALE before the exclusive phase.
#                 Deliberately NOT --remove-all, which would rip the lock out from under a
#                 legitimately running parallel backup. Note what plain `unlock` can and
#                 cannot do: it removes a lock older than 30 min, OR one whose process is
#                 dead ON THE SAME HOSTNAME. Our hostname is the container id and every
#                 deploy makes a new one, so against a lock orphaned by the PREVIOUS
#                 container only the 30-minute rule applies — `unlock` alone does NOT heal
#                 a freshly orphaned lock, which is exactly why --retry-lock is there too.
#                 (Within THIS container the same-host dead-pid rule does apply, which is
#                 what makes the SIGTERM trap below effective immediately.)
#   trap TERM     releases our own lock when a deploy stops the container mid-run — the
#                 case that actually caused the incident. Needs entrypoint.sh to relay
#                 SIGTERM: busybox crond, the sidecar's PID 1, ignores it and forwards
#                 nothing (measured), so before that change no signal ever arrived here.
#
# TIME BUDGET (work/backup-leg-timeout-and-probe-alert). restic has no overall deadline of
# its own and its S3 backend retries with a long backoff: `restic cat config` against an
# unreachable bucket was MEASURED at over ten minutes on this image. Nothing in here bounded
# that, so one wedged endpoint could keep a run alive past the next cron tick and stack runs
# on top of each other. LEG_TIMEOUT_SECONDS below is a ceiling on a WHOLE leg — see
# leg_remaining().
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
# How long a restic call waits for a conflicting repository lock before giving up. 15m
# covers the real case — an overlapping run whose prune is still going — while keeping the
# worst case bounded: four exclusive operations across two legs cannot stall the run past
# ~1 h, well inside the 6 h cron interval, so a wedged repo still fails (and alerts) within
# one cycle instead of piling runs on top of each other.
RETRY_LOCK="${RESTIC_RETRY_LOCK:-15m}"
# Wall-clock ceiling on ONE leg (both legs run in sequence). Sized by what has to fit inside
# it, in order:
#   3 x RETRY_LOCK = 45 min — backup, forget and check may EACH wait out a conflicting lock,
#     and a budget below that would silently defeat the retry window that exists to survive a
#     stale lock;
#   + the real work — the heaviest leg measured (2026-08-28: collapsing 424 snapshots and
#     18.7 GiB in one forget --prune) took about a minute, so the remaining 45 min is ~45x
#     headroom over the worst run actually observed.
# 90 min also keeps the whole run bounded at 2 x 90 = 3 h, HALF the 6 h cron interval, so a
# wedged endpoint can never leave a run still going when the next tick fires.
# Asserted against RETRY_LOCK and entrypoint.sh's cron default by BackupSidecarScriptTests.
LEG_TIMEOUT_SECONDS="${BACKUP_LEG_TIMEOUT_SECONDS:-5400}"
# Epoch by which the leg in flight must be done. run_leg sets it per leg; push() runs in a
# subshell of run_leg and so inherits it. 0 = no leg in flight.
LEG_DEADLINE=0
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-us-east-1}"

# The repo the leg in flight is working on, for the SIGTERM trap below. push() exports the
# S3 credentials PER LEG, so "which repo would we have to unlock" only exists at runtime —
# the script has no other notion of a current repository.
CURRENT_REPO=""

# The file run_leg is capturing the leg in flight into, for the same trap. Empty at top level
# between legs, and deliberately blanked inside push() — see on_term.
CURRENT_LEG_LOG=""

STATE_DIR="${STATE_DIR:-/state}"
ALERT_REPEAT_HOURS="${ALERT_REPEAT_HOURS:-24}"
TELEGRAM_BOT_TOKEN="${TELEGRAM_BOT_TOKEN:-}"
TELEGRAM_CHAT_ID="${TELEGRAM_CHAT_ID:-}"
HEALTHCHECK_URL="${HEALTHCHECK_URL:-}"

log() { echo "[backup $(date -u +%FT%TZ)] $*"; }

# Deploy-time lock release. A trap runs only AFTER the foreground command returns, so this
# fires once the restic that was signalled alongside us has exited — leaving its lock behind
# is precisely the failure we are cleaning up after. Plain `unlock` (never --remove-all)
# suffices here: the dead restic held the lock on THIS hostname, which restic itself scores
# as stale, so the sweep is immediate and cannot touch anyone else's lock.
on_term() {
	trap '' TERM INT
	# Replay the leg's captured output FIRST. run_leg only `cat`s the log after its subshell
	# returns, and on shutdown this handler runs instead of that `cat` and exits — so the leg's
	# own "SIGTERM during a leg on <repo>" line, written inside the subshell, never reached
	# `docker logs` and a shutdown could not be diagnosed at all (the unlock did happen; it was
	# just invisible). Same replay now covers a leg killed by its time budget.
	# CURRENT_LEG_LOG is empty in push()'s re-armed copy of this handler, where stdout ALREADY
	# is that file and echoing it back would append the file to itself.
	[ -n "$CURRENT_LEG_LOG" ] && [ -f "$CURRENT_LEG_LOG" ] && cat "$CURRENT_LEG_LOG" || true
	if [ -n "$CURRENT_REPO" ]; then
		log "SIGTERM during a leg on $CURRENT_REPO — releasing our repository lock"
		restic -r "$CURRENT_REPO" unlock >/dev/null 2>&1 			|| log "WARNING: unlock on shutdown failed — a stale lock may survive until it ages out (30 min)"
	else
		log "SIGTERM — no leg in flight, nothing to unlock"
	fi
	exit 143
}
# Armed here for the top-level shell, and AGAIN inside push(): ash resets traps to their
# default action in a ( ) subshell (verified on this image), and each leg's restic runs in
# exactly such a subshell — see run_leg. Arming it only here would be dead code where it
# matters most.
trap on_term TERM INT

newest="$(ls -1d "$DATA_DIR"/backups/*-auto/ 2>/dev/null | sort | tail -1 || true)"
if [ -z "$newest" ]; then
	log "ERROR: no *-auto snapshot under $DATA_DIR/backups — is the data volume mounted?"
	exit 1
fi
newest="${newest%/}"
log "source snapshot: $newest"

# The named log exclusion — see the header. Applied to BOTH legs: PetBox's own log
# dbs (data/logs/{project}/{log}.db, mirrored into the snapshot set as
# <set>/logs/**) are telemetry, not data, and never go offsite. Everything else in
# the set is data and IS pushed: petbox.db, deploy.db, db/**, memory/**, tasks/**,
# sessions/**, config/** (+ $DATA_DIR/keys, the DataProtection key ring).
EXCLUDE_LOGS="--exclude $newest/logs"

# Seconds left of the current leg's budget. Floored at 1, never 0: `timeout 0` means NO limit
# in both busybox and coreutils, so an exhausted budget must not round down into "run forever".
# A 1 s timeout is the intended behaviour there — the leg is over, fail it now.
leg_remaining() {
	_left=$(( LEG_DEADLINE - $(date +%s) ))
	[ "$_left" -gt 0 ] || _left=1
	echo "$_left"
}

# push REPO TAG EXTRA_ARGS ACCESS_KEY SECRET_KEY
# Every restic call below is wrapped in `timeout "$(leg_remaining)"`, not a fixed per-call
# limit: the budget belongs to the LEG, so backup + unlock + forget + check together cannot
# outlast it however the time is distributed between them. `timeout` sends SIGTERM, which is
# exactly what restic needs to drop its repository lock on the way out — the trap above and
# this ceiling clean up after the same failure, they do not compete. A tripped timeout leaves
# a non-zero status, so the subshell's `set -e` aborts the leg and it counts as FAILED
# (and alerts) like any other broken leg.
push() {
	repo="$1"; tag="$2"; extra="$3"
	export AWS_ACCESS_KEY_ID="$4"
	export AWS_SECRET_ACCESS_KEY="$5"
	CURRENT_REPO="$repo"
	CURRENT_LEG_LOG=""      # our stdout IS the leg log here; on_term must not append it to itself
	trap on_term TERM INT   # re-armed: this runs in run_leg's subshell, where ash reset it
	timeout "$(leg_remaining)" restic -r "$repo" snapshots >/dev/null 2>&1 || { log "init $tag repo"; timeout "$(leg_remaining)" restic -r "$repo" init; }
	log "backup $tag -> $repo"
	# --group-by host,tags on BOTH backup and forget. The source path is
	# /data/backups/<timestamp>-auto and so has a different name every run, so restic's
	# default grouping (host,paths) put every run in a group of its own: backup never found
	# a parent snapshot ("will read all files", every time) and forget kept a full
	# 7-daily/4-weekly set FOR EACH RUN — 429 snapshots had accumulated in the full repo by
	# 2026-08-28. Grouping by host+tags is what the tag actually means: one timeline per leg.
	# EXPECT THE FIRST forget --prune AFTER THIS CHANGE TO DELETE A LOT.
	# shellcheck disable=SC2086 — $extra is an intentional word-split of restic flags
	timeout "$(leg_remaining)" restic -r "$repo" backup "$newest" "$DATA_DIR/keys" --tag "$tag" --host petbox 		--group-by host,tags --retry-lock "$RETRY_LOCK" $extra
	# Stale-lock sweep immediately before the exclusive phase — as late as possible, so a
	# lock that went stale while the backup above was running is caught too. Not fatal on
	# its own: --retry-lock may still get the exclusive operations through.
	timeout "$(leg_remaining)" restic -r "$repo" unlock || log "WARNING: stale-lock sweep failed on $tag"
	timeout "$(leg_remaining)" restic -r "$repo" forget --tag "$tag" --keep-daily "$KEEP_DAILY" --keep-weekly "$KEEP_WEEKLY" 		--group-by host,tags --prune --retry-lock "$RETRY_LOCK"
	timeout "$(leg_remaining)" restic -r "$repo" check --retry-lock "$RETRY_LOCK"
	CURRENT_REPO=""
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
	CURRENT_LEG_LOG="$_log"
	LEG_DEADLINE=$(( $(date +%s) + LEG_TIMEOUT_SECONDS ))
	(set -e; push "$@") >"$_log" 2>&1
	_status=$?
	CURRENT_LEG_LOG=""
	cat "$_log"
	if [ "$_status" -eq 0 ]; then
		log "$_name ok"
	elif [ "$(date +%s)" -ge "$LEG_DEADLINE" ]; then
		# Decided from the clock, not from timeout's exit status: busybox and coreutils do not
		# agree on what that status is, and this line is diagnosis, not control flow — the leg
		# has already failed either way.
		log "$_name FAILED: hit the ${LEG_TIMEOUT_SECONDS}s leg time budget — restic was sent SIGTERM, which releases its repository lock"
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

# ── compact -> R2 (data, no logs) ──
run_leg compact /tmp/backup-compact.log \
	"s3:${R2_S3_ENDPOINT}/${R2_BUCKET}/compact" compact "$EXCLUDE_LOGS" \
	"$R2_ACCESS_KEY_ID" "$R2_SECRET_ACCESS_KEY"
compact_ok=$?

# ── full -> FirstVDS S3 (data, no logs — the "full" tag is historical; the repo
# names/tags stay as-is so existing restic retention keeps working) ──
run_leg full /tmp/backup-full.log \
	"s3:${FVDS_S3_ENDPOINT}/${FVDS_BUCKET}/full" full "$EXCLUDE_LOGS" \
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
