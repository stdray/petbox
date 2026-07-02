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
// Plain TS for native node type-stripping: zero deps.

import type { Msg } from "./transcript.ts";

// How many trailing messages to optimistically resend when the server cursor is unknown.
// A turn typically adds 2-4 messages; overlap is idempotent, so oversizing only costs bytes.
const OVERLAP_WINDOW = 8;

const MAX_APPEND_ATTEMPTS = 3;

export type PushTarget = {
  baseUrl: string;
  project: string;
  sessionId: string;
  apiKey: string;
  agent: string;
  timeoutMs: number;
};

function ndjson(msgs: readonly Msg[]): string {
  return msgs.map((m) => JSON.stringify(m)).join("\n");
}

async function post(url: string, apiKey: string, body: string, timeoutMs: number): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetch(url, {
      method: "POST",
      headers: { "X-Api-Key": apiKey, "Content-Type": "application/x-ndjson; charset=utf-8" },
      body,
      signal: ctrl.signal,
    });
  } finally {
    clearTimeout(timer);
  }
}

// Push the (full, ordered) local transcript incrementally. `knownLastOrdinal` is the cursor
// remembered from a previous response in THIS process, or null when unknown. Returns the
// server's lastOrdinal after the push (feed it back next call), or null when every path
// failed — callers are best-effort and must swallow that.
export async function pushTranscript(
  t: PushTarget,
  msgs: readonly Msg[],
  knownLastOrdinal: number | null,
): Promise<number | null> {
  if (msgs.length === 0) return knownLastOrdinal;

  // The server already has everything we know about → nothing to send (the transcript is
  // append-only, so equal length means equal content).
  if (knownLastOrdinal !== null && knownLastOrdinal >= msgs.length) return knownLastOrdinal;

  const base = `${t.baseUrl}/api/sessions/${t.project}/${encodeURIComponent(t.sessionId)}`;

  let from =
    knownLastOrdinal !== null && knownLastOrdinal >= 0
      ? knownLastOrdinal + 1
      : Math.max(1, msgs.length - OVERLAP_WINDOW + 1);

  for (let attempt = 0; attempt < MAX_APPEND_ATTEMPTS; attempt++) {
    let resp: Response;
    try {
      resp = await post(
        `${base}/append?agent=${encodeURIComponent(t.agent)}&fromOrdinal=${from}`,
        t.apiKey,
        ndjson(msgs.slice(from - 1)),
        t.timeoutMs,
      );
    } catch {
      return null; // network failure — a full-snapshot retry would fail the same way
    }

    if (resp.ok) {
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

    // 404 = old server without the append route; anything else = unknown failure.
    // Either way the legacy full-snapshot push is the safe fallback.
    break;
  }

  try {
    const resp = await post(`${base}?agent=${encodeURIComponent(t.agent)}`, t.apiKey, ndjson(msgs), t.timeoutMs);
    if (!resp.ok) return null;
    const j = (await resp.json().catch(() => null)) as { version?: number } | null;
    return j && typeof j.version === "number" ? j.version : msgs.length;
  } catch {
    return null;
  }
}
