// Shared incremental session push — the ONE append-flow implementation both Stop hooks
// (push-session.ts for Claude Code, opencode-plugin.ts for opencode) use, so the wire
// protocol cannot drift between agents (spec: session-append-wire, wiring-single-source).
//
// The server owns the cursor: a session's lastOrdinal is its stored message count. The
// client sends only a tail batch tagged with `fromOrdinal` (the ordinal of the batch's
// first message):
//   - contiguous / overlapping → 200 { lastOrdinal, appended } (overlap is idempotent —
//     ordinals the server already holds are ignored, so guessing "a little too early"
//     is always safe);
//   - gap → 409 { error: "gap", lastOrdinal } → self-heal: resend from lastOrdinal+1.
//
// The client keeps NO durable state. A long-lived host (the opencode plugin) passes the
// lastOrdinal remembered from the previous response; a per-invocation host (the Claude
// Code Stop hook process is fresh each turn) passes null and we optimistically resend a
// small overlap window from the end of the local transcript — one round-trip in the
// steady state, two after a restart/outage (the 409 tells us where to resume).
//
// Fallback: an old server without the append route 404s → push the full transcript to the
// legacy last-write-wins endpoint, exactly what the hooks did before.
//
// Observed role binding (binding-not-server-authoritative): when ~/.petbox/roles.json has
// roles for the agent, stamp X-PetBox-Session-Meta with { roleBinding }. Best-effort —
// missing roles.json never fails the push. Server stores as session MetaJson observation
// only; local roles.json remains the source of truth.
//
// subagentRuns (spec: subagent-run-provenance) rides the same free-form meta JSON, alongside
// roleBinding: roleBinding is the machine's role→model INTENTION, subagentRuns is the FACT of
// which subagents actually ran (from transcript.ts's collectSubagentRuns / droid-transcript.ts's
// collectDroidSubagentRuns). Callers pass it in as `extraMeta`; absent/empty → key omitted
// entirely, never an empty array on the wire.
//
// Plain TS for native node type-stripping: zero deps.

import { resolveObservedBinding } from "./roles.ts";
import type { Msg } from "./transcript.ts";
import { wireLog } from "./wire-log.ts";

// How many trailing messages to optimistically resend when the server cursor is unknown.
// A turn typically adds 2-4 messages; overlap is idempotent, so oversizing only costs bytes.
const OVERLAP_WINDOW = 8;

const MAX_APPEND_ATTEMPTS = 3;

// --- Transient-failure retry (bug: push-transcript-503-session-not-persisted) ---------------
// Orthogonal to MAX_APPEND_ATTEMPTS above: that loop resends from a NEW cursor after a
// structured 409 (self-heal, not a retry). This retry resends the SAME request after a
// TRANSIENT failure (503 etc., or a network-level throw) that has nothing to do with cursor
// position. Applies to both the increment endpoint and the legacy full-snapshot fallback (a
// server mid-restart 503s both routes the same way).
//
// Retryable: "come back later" (408/429) and "the server itself is broken/restarting"
// (500/502/503/504). Everything else is terminal for THIS loop — 2xx is handled separately,
// 409 is the gap-cursor protocol signal (not an error), 404 means "old server, no append
// route", and any other 4xx is a request-shape problem retrying won't fix.
const RETRYABLE_STATUSES = new Set([408, 429, 500, 502, 503, 504]);

// Up to 4 tries (1 + 3 retries) per phase — a cap in COUNT, independent of the deadline below,
// so a fast-failing server doesn't get hammered indefinitely just because time remains.
const MAX_RETRY_ATTEMPTS = 4;

// Hard wall-clock ceiling for ALL retry activity in one pushTranscript call — the increment
// phase and, if it falls through, the legacy phase SHARE this one clock, so a call that retries
// in both phases still cannot exceed it. This runs inside a Stop hook, which must not noticeably
// stall the user's turn — the same invariant SessionStart's canon fetch already keeps (its own
// single-request budget, canon.ts's FETCH_TIMEOUT_MS, is 8000ms). 10s is the smallest round
// number comfortably above that single-request reference point while still leaving room for a
// few short retries: long enough that a live-but-restarting server (503s typically arrive in
// tens/hundreds of ms) gets a real chance to recover, short enough that a dead peer cannot turn
// one Stop hook into a multi-attempt, multi-minute stall.
//
// The part that actually matters for a DEAD SOCKET (vs. a live server answering 503 fast) is
// that every individual attempt's own timeout is ADDITIONALLY capped to whatever remains of
// this budget (see attemptWithRetry) — without that, a single stalled attempt could each burn
// the full per-request t.timeoutMs (12000ms in push-session.ts / droid-push-session.ts), so two
// stalled attempts alone would already blow past a deadline that was only checked BETWEEN
// attempts.
const RETRY_DEADLINE_MS = 10_000;

function isRetryableStatus(status: number): boolean {
  return RETRYABLE_STATUSES.has(status);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Exponential backoff with equal jitter (0.5x-1.5x of the base) so a batch of parallel sessions
// hitting the same restarting server don't all retry in lockstep. Base doubles from 250ms and
// caps at 2000ms: across the 3 backoff gaps between 4 attempts that averages roughly
// 250+500+1000 = 1750ms, leaving multiple seconds of the 10s deadline free for actual request
// round trips even in the worst realistic (fast-503) case.
function backoffDelayMs(attempt: number): number {
  const base = Math.min(250 * 2 ** attempt, 2000);
  return base * (0.5 + Math.random());
}

export type PushTarget = {
  baseUrl: string;
  project: string;
  sessionId: string;
  apiKey: string;
  agent: string;
  timeoutMs: number;
};

/**
 * Pure helper: JSON for X-PetBox-Session-Meta, or null when there's nothing to say / any error.
 * Shape: { roleBinding?: { profile, agent, roles: { role: model } }, ...extraMeta }
 * `extraMeta` (e.g. { subagentRuns } — see transcript.ts) is merged in as-is; the caller is
 * responsible for not passing empty/garbage keys (an empty subagentRuns array should never
 * reach here — see push-session.ts / droid-push-session.ts).
 */
export function buildSessionMetaHeader(
  agent: string,
  homeDir?: string,
  extraMeta?: Record<string, unknown>,
): string | null {
  try {
    const obs =
      homeDir === undefined
        ? resolveObservedBinding(agent)
        : resolveObservedBinding(agent, homeDir);
    const meta: Record<string, unknown> = {};
    if (obs) meta["roleBinding"] = obs;
    if (extraMeta) Object.assign(meta, extraMeta);
    if (Object.keys(meta).length === 0) return null;
    return JSON.stringify(meta);
  } catch (e) {
    // Not the ordinary "no roles.json yet" path (loadRoles itself never throws) — reaching here
    // means something unexpected broke building the session-meta header (e.g. non-serializable
    // extraMeta). Best-effort: the push still proceeds without the header, but leave a trace.
    wireLog("append", `buildSessionMetaHeader(agent=${agent}) unexpected failure — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return null;
  }
}

function ndjson(msgs: readonly Msg[]): string {
  return msgs.map((m) => JSON.stringify(m)).join("\n");
}

async function post(
  url: string,
  apiKey: string,
  body: string,
  timeoutMs: number,
  metaHeader: string | null,
): Promise<Response> {
  const headers: Record<string, string> = {
    "X-Api-Key": apiKey,
    "Content-Type": "application/x-ndjson; charset=utf-8",
    // Connection: close — no lingering keep-alive socket after this short-lived hook
    // process's request (see canon.ts's fetchCanon for the full rationale).
    Connection: "close",
  };
  if (metaHeader) headers["X-PetBox-Session-Meta"] = metaHeader;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetch(url, {
      method: "POST",
      headers,
      body,
      signal: ctrl.signal,
    });
  } finally {
    clearTimeout(timer);
  }
}

type PostOutcome = { kind: "response"; resp: Response } | { kind: "network"; error: unknown };

// Retries post() against ONE fixed url+body while the outcome is transient (a RETRYABLE_STATUSES
// status, or a thrown network error), up to MAX_RETRY_ATTEMPTS tries, never running past
// `deadline` (an absolute Date.now()-scale timestamp shared across every attempt in this
// pushTranscript call — see RETRY_DEADLINE_MS). Returns whatever the LAST attempt produced —
// a settled response (2xx, 409, 404, or an exhausted retryable status) or an exhausted network
// failure — plus how many attempts it actually took (1 when the first try already settled it,
// so callers can stay silent on the fast/normal path and only log when retries actually fired).
async function attemptWithRetry(
  url: string,
  apiKey: string,
  body: string,
  baseTimeoutMs: number,
  metaHeader: string | null,
  deadline: number,
): Promise<{ outcome: PostOutcome; attempts: number }> {
  let outcome: PostOutcome = {
    kind: "network",
    error: new Error("retry deadline exceeded before first attempt"),
  };
  let attempts = 0;
  for (let attempt = 0; attempt < MAX_RETRY_ATTEMPTS; attempt++) {
    const remaining = deadline - Date.now();
    if (remaining <= 0) break; // out of budget — the loop-entry outcome above stands
    attempts++;
    try {
      // Cap THIS attempt's own timeout to whatever remains of the shared deadline — this is
      // what actually protects against a dead socket (see RETRY_DEADLINE_MS's comment): without
      // it a single stalled attempt could burn the full baseTimeoutMs regardless of how little
      // budget is left.
      const resp = await post(url, apiKey, body, Math.min(baseTimeoutMs, remaining), metaHeader);
      outcome = { kind: "response", resp };
      if (resp.ok || !isRetryableStatus(resp.status)) return { outcome, attempts };
    } catch (e) {
      outcome = { kind: "network", error: e };
    }
    if (attempt === MAX_RETRY_ATTEMPTS - 1) break;
    const remainingAfter = deadline - Date.now();
    if (remainingAfter <= 0) break;
    await sleep(Math.min(backoffDelayMs(attempt), remainingAfter));
  }
  return { outcome, attempts };
}

// Push the (full, ordered) local transcript incrementally. `knownLastOrdinal` is the cursor
// remembered from a previous response in THIS process, or null when unknown. Returns the
// server's lastOrdinal after the push (feed it back next call), or null when every path
// failed — callers are best-effort and must swallow that.
export async function pushTranscript(
  t: PushTarget,
  msgs: readonly Msg[],
  knownLastOrdinal: number | null,
  extraMeta?: Record<string, unknown>,
): Promise<number | null> {
  if (msgs.length === 0) return knownLastOrdinal;

  // The server already has everything we know about → nothing to send (the transcript is
  // append-only, so equal length means equal content).
  if (knownLastOrdinal !== null && knownLastOrdinal >= msgs.length) return knownLastOrdinal;

  const base = `${t.baseUrl}/api/sessions/${t.project}/${encodeURIComponent(t.sessionId)}`;
  // Stamp observed binding (+ any extra meta, e.g. subagentRuns) once per push; never let
  // roles.json issues or extra-meta computation fail the transcript push itself.
  const metaHeader = buildSessionMetaHeader(t.agent, undefined, extraMeta);

  // Shared clock for ALL retry activity below (increment phase + legacy phase both draw from
  // the same budget — see RETRY_DEADLINE_MS).
  const deadline = Date.now() + RETRY_DEADLINE_MS;

  let from =
    knownLastOrdinal !== null && knownLastOrdinal >= 0
      ? knownLastOrdinal + 1
      : Math.max(1, msgs.length - OVERLAP_WINDOW + 1);

  for (let attempt = 0; attempt < MAX_APPEND_ATTEMPTS; attempt++) {
    const { outcome, attempts: tries } = await attemptWithRetry(
      `${base}/append?agent=${encodeURIComponent(t.agent)}&fromOrdinal=${from}`,
      t.apiKey,
      ndjson(msgs.slice(from - 1)),
      t.timeoutMs,
      metaHeader,
      deadline,
    );

    if (outcome.kind === "network") {
      // Network failure, even after retrying it in place — a full-snapshot retry would fail
      // the same way, so we give up here rather than trying it. Best-effort (callers swallow
      // this null): one trace per failed push, not per network blip inside a healthy retry.
      wireLog(
        "append",
        `pushTranscript network failure for ${t.project}/${t.sessionId} (agent=${t.agent}) after ${tries} attempt(s) — ` +
          `${outcome.error instanceof Error ? outcome.error.message : String(outcome.error)} — session NOT persisted this turn`,
      );
      return null;
    }

    const resp = outcome.resp;

    if (resp.ok) {
      if (tries > 1) {
        // Honest success trace: the turn nearly went missing (>1 attempt means at least one
        // prior try failed) but the retry recovered it — this is the "doesn't just go silent
        // on success" half of the card's acceptance criteria.
        wireLog(
          "append",
          `pushTranscript for ${t.project}/${t.sessionId} (agent=${t.agent}) succeeded after ${tries} attempt(s) (last status ${resp.status}) — session persisted`,
        );
      }
      const j = (await resp.json().catch(() => null)) as { lastOrdinal?: number } | null;
      return j && typeof j.lastOrdinal === "number" ? j.lastOrdinal : msgs.length;
    }

    if (resp.status === 409) {
      // Structured contiguity gap: the body carries the server's cursor. Resend from there.
      const j = (await resp.json().catch(() => null)) as { lastOrdinal?: number } | null;
      const last = j && typeof j.lastOrdinal === "number" ? j.lastOrdinal : null;
      if (last === null) break; // unparseable reject → full-snapshot fallback
      if (last >= msgs.length) return last; // server is already ahead of our local view
      from = last + 1;
      continue;
    }

    // 404 (old server without the append route) or a retryable status already retried to
    // exhaustion (or any other unclassified status) — the safe fallback is the legacy
    // full-snapshot push, as before.
    break;
  }

  const { outcome: legacyOutcome, attempts: legacyTries } = await attemptWithRetry(
    `${base}?agent=${encodeURIComponent(t.agent)}`,
    t.apiKey,
    ndjson(msgs),
    t.timeoutMs,
    metaHeader,
    deadline,
  );

  if (legacyOutcome.kind === "network") {
    wireLog(
      "append",
      `pushTranscript legacy fallback network failure for ${t.project}/${t.sessionId} ` +
        `(agent=${t.agent}) after ${legacyTries} attempt(s) — ` +
        `${legacyOutcome.error instanceof Error ? legacyOutcome.error.message : String(legacyOutcome.error)} — session NOT persisted this turn`,
    );
    return null;
  }

  const resp = legacyOutcome.resp;
  if (!resp.ok) {
    wireLog(
      "append",
      `pushTranscript legacy fallback for ${t.project}/${t.sessionId} (agent=${t.agent}) got ` +
        `HTTP ${resp.status} after ${legacyTries} attempt(s) — session NOT persisted this turn`,
    );
    return null;
  }
  if (legacyTries > 1) {
    wireLog(
      "append",
      `pushTranscript legacy fallback for ${t.project}/${t.sessionId} (agent=${t.agent}) succeeded after ${legacyTries} attempt(s) (last status ${resp.status}) — session persisted`,
    );
  }
  const j = (await resp.json().catch(() => null)) as { version?: number } | null;
  return j && typeof j.version === "number" ? j.version : msgs.length;
}
