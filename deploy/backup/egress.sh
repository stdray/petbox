#!/bin/sh
# EGRESS PATH SELECTION — direct first, an egress proxy only as a FALLBACK, decided PER LEG.
#
# Why this file exists (work/backup-leg-direct-first-proxy-fallback, 2026-11-16). From apps1, TCP
# 80/443 to firsts3.ru (37.46.135.136) timed out on 5 attempts out of 5, while ICMP answered and
# Cloudflare R2 answered directly: the network in front of 37.46.135.0/24 drops TCP from this host's
# prefix. What hid it was restic's own error, which names four suspects in one line — "unreachable
# endpoint, bad credentials, wrong RESTIC_PASSWORD or TLS" — and its S3 backend spends MINUTES of
# backoff deciding which one it was. A `curl` probe asks the only question that matters here
# ("does TCP+TLS to the endpoint host work at all?") in seconds, so the path is chosen HERE and
# restic is left to do the work it was built for.
#
# Two properties this must not lose:
#   * DIRECT IS THE PRIORITY, and the choice is PER LEG. The first fix for the incident forced a
#     container-wide HTTPS_PROXY, which also routed the R2 leg — perfectly reachable — through a
#     proxy on another host: an extra hop and an extra dependency for a leg that needs neither.
#   * THE PROXY IS NEVER EXPORTED GLOBALLY. egress_env is called inside the subshell of a single
#     leg, so it reaches that leg's restic and nothing else — not the other leg, not the
#     alert/heartbeat curls, not the probes.
#
# Sourced by BOTH entrypoint.sh and backup.sh on purpose: the start-up probe and the scheduled run
# must never disagree about which path a leg will take, so the rule gets one implementation.

# curl exits 0 for ANY HTTP answer it receives — 401/403/404 included — and that is exactly the
# signal wanted: the question is whether a TCP connection and a TLS handshake to this host work,
# not whether the caller is authorised. Only a connect/TLS/timeout failure is non-zero.
EGRESS_PROBE_CONNECT_TIMEOUT="${EGRESS_PROBE_CONNECT_TIMEOUT:-4}"
EGRESS_PROBE_MAX_TIME="${EGRESS_PROBE_MAX_TIME:-8}"

# egress_host ENDPOINT -> bare host[:port] to probe. Taken from the endpoint itself
# (R2_S3_ENDPOINT / FVDS_S3_ENDPOINT), never hardcoded: moving a repo to a new endpoint moves its
# probe with it.
egress_host() {
	_eg_rest="${1#*://}"
	printf '%s' "${_eg_rest%%/*}"
}

# egress_probe HOST [PROXY_URL] -> 0 when https://HOST/ answers, through PROXY_URL when given.
egress_probe() {
	_eg_host="$1"
	_eg_proxy="${2:-}"
	if [ -n "$_eg_proxy" ]; then
		curl -x "$_eg_proxy" --connect-timeout "$EGRESS_PROBE_CONNECT_TIMEOUT" \
			--max-time "$EGRESS_PROBE_MAX_TIME" -sS -o /dev/null "https://$_eg_host/" >/dev/null 2>&1
	else
		# --noproxy '*': the DIRECT probe must be direct even when an ambient HTTPS_PROXY is set,
		# or a live proxy would make every path look direct and the fallback would never trigger.
		curl --noproxy '*' --connect-timeout "$EGRESS_PROBE_CONNECT_TIMEOUT" \
			--max-time "$EGRESS_PROBE_MAX_TIME" -sS -o /dev/null "https://$_eg_host/" >/dev/null 2>&1
	fi
}

egress_direct_ok() {
	egress_probe "$1"
}

# The proxied probe proves BOTH halves of the fallback: that the proxy is up, and that it can reach
# this particular host. A proxy that answers but cannot CONNECT to the endpoint is not an
# alternative path, and treating it as one would just re-fail inside restic.
egress_proxy_ok() {
	[ -n "${BACKUP_HTTPS_PROXY:-}" ] || return 1
	egress_probe "$1" "$BACKUP_HTTPS_PROXY"
}

# egress_available PATH HOST -> 0 when PATH is usable for HOST right now.
egress_available() {
	case "$1" in
	direct) egress_direct_ok "$2" ;;
	proxy) egress_proxy_ok "$2" ;;
	*) return 1 ;;
	esac
}

# egress_other PATH -> the complementary path.
egress_other() {
	if [ "$1" = "direct" ]; then
		printf 'proxy'
	else
		printf 'direct'
	fi
}

# egress_env PATH — point THIS shell at PATH. MUST be called inside the subshell that runs a single
# leg (see push_once in backup.sh): it exports HTTPS_PROXY on the proxy path and clears it on the
# direct one, so calling it at top level would leak the proxy onto the other leg, onto the
# alert/heartbeat curls and onto the probes above.
egress_env() {
	case "$1" in
	proxy)
		HTTPS_PROXY="${BACKUP_HTTPS_PROXY:-}"
		https_proxy="$HTTPS_PROXY"
		export HTTPS_PROXY https_proxy
		;;
	*)
		unset HTTPS_PROXY https_proxy
		;;
	esac
}
