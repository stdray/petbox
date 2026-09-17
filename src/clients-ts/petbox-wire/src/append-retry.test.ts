// Tests for append.ts's transient-failure retry (bug: push-transcript-503-session-not-persisted).
// Before this fix, ANY non-ok/non-409 status (including 503) or network throw fell straight
// into the legacy full-snapshot fallback with no retry anywhere — a server 503ing mid-restart
// lost the turn permanently, and the ONLY trace was the "session NOT persisted this turn" log
// line, written once, with nothing after it.
//
// Exercised against an in-process fake HTTP server (same pattern as canon.test.ts), never a
// spawned child process — pushTranscript is a plain async function.
//
// Run: node --test src/append-retry.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test, mock } from "node:test";
import { pushTranscript, type PushTarget } from "./append.ts";
import type { Msg } from "./transcript.ts";
import { wireLogPath } from "./wire-log.ts";

function startFakeServer(
  handler: (req: http.IncomingMessage, res: http.ServerResponse) => void,
): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  return new Promise((resolve) => {
    const server = http.createServer(handler);
    server.listen(0, "127.0.0.1", () => {
      const port = (server.address() as { port: number }).port;
      resolve({
        baseUrl: `http://127.0.0.1:${port}`,
        close: () => new Promise((r) => server.close(() => r())),
      });
    });
  });
}

function restoreEnv(key: string, prev: string | undefined): void {
  if (prev === undefined) delete process.env[key];
  else process.env[key] = prev;
}

// wireLog() (called with no explicit homeDir from append.ts) resolves via node:os homedir(),
// which reads HOME on POSIX and USERPROFILE on win32 — isolate both so this test's log
// assertions never see another test's (or a real dev machine's) ~/.petbox/wire.log.
async function withIsolatedHome<T>(fn: (home: string) => Promise<T>): Promise<T> {
  const home = mkdtempSync(join(tmpdir(), "petbox-append-retry-test-"));
  const prevHome = process.env["HOME"];
  const prevProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home;
  try {
    return await fn(home);
  } finally {
    restoreEnv("HOME", prevHome);
    restoreEnv("USERPROFILE", prevProfile);
    rmSync(home, { recursive: true, force: true });
  }
}

function wireLogText(home: string): string {
  try {
    return readFileSync(wireLogPath(home), "utf8");
  } catch {
    return "";
  }
}

function target(baseUrl: string): PushTarget {
  return {
    baseUrl,
    project: "fake-project",
    sessionId: "fake-session",
    apiKey: "fake-key",
    agent: "claude-code",
    timeoutMs: 12000, // same value push-session.ts/droid-push-session.ts pass as FETCH_TIMEOUT_MS
  };
}

const MSGS: Msg[] = [
  { role: "user", content: "hi" },
  { role: "assistant", content: "hello" },
];

test("append: 503 then 200 — the turn lands on retry, and wire.log shows it was resent, not just lost", async () => {
  await withIsolatedHome(async (home) => {
    let appendRequests = 0;
    const { baseUrl, close } = await startFakeServer((req, res) => {
      if (req.url?.includes("/append")) {
        appendRequests++;
        if (appendRequests < 3) {
          res.writeHead(503, { "Content-Type": "application/json" }).end(JSON.stringify({ error: "restarting" }));
          return;
        }
        res.writeHead(200, { "Content-Type": "application/json" }).end(
          JSON.stringify({ lastOrdinal: MSGS.length, appended: MSGS.length }),
        );
        return;
      }
      // The legacy route must never be hit — a retryable status must be retried in place, not
      // used as a trigger to fall into the full-snapshot fallback (that was the old bug).
      res.writeHead(500).end();
    });
    try {
      const result = await pushTranscript(target(baseUrl), MSGS, null);
      assert.equal(result, MSGS.length, "the retry must eventually return the server's cursor, not null");
      assert.equal(appendRequests, 3, "exactly 2 failures + 1 success on the SAME increment endpoint, no legacy fallthrough");

      const log = wireLogText(home);
      assert.match(
        log,
        /pushTranscript for fake-project\/fake-session \(agent=claude-code\) succeeded after 3 attempt\(s\) \(last status 200\) — session persisted/,
        `wire.log must show the turn was resent and landed, not silence, got:\n${log}`,
      );
    } finally {
      await close();
    }
  });
});

test("append: connection reset then 200 — a network-level throw is retried too, not given up on immediately", async () => {
  await withIsolatedHome(async () => {
    let appendRequests = 0;
    const { baseUrl, close } = await startFakeServer((req, res) => {
      if (req.url?.includes("/append")) {
        appendRequests++;
        if (appendRequests === 1) {
          req.socket.destroy(); // simulate a reset mid-restart — fetch() throws, not a status
          return;
        }
        res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify({ lastOrdinal: MSGS.length }));
        return;
      }
      res.writeHead(500).end();
    });
    try {
      const result = await pushTranscript(target(baseUrl), MSGS, null);
      assert.equal(result, MSGS.length, "a network throw must be retried in place, not an immediate null");
      assert.equal(appendRequests, 2);
    } finally {
      await close();
    }
  });
});

test("append: 409 gap self-heal is unaffected by the new retry loop (orthogonal mechanisms, not to be confused)", async () => {
  await withIsolatedHome(async () => {
    let appendRequests = 0;
    const { baseUrl, close } = await startFakeServer((req, res) => {
      if (req.url?.includes("fromOrdinal=1")) {
        appendRequests++;
        res.writeHead(409, { "Content-Type": "application/json" }).end(JSON.stringify({ error: "gap", lastOrdinal: 1 }));
        return;
      }
      if (req.url?.includes("fromOrdinal=2")) {
        appendRequests++;
        res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify({ lastOrdinal: 2 }));
        return;
      }
      res.writeHead(500).end();
    });
    try {
      const result = await pushTranscript(target(baseUrl), MSGS, null);
      assert.equal(result, 2, "the 409 cursor self-heal must still resend from the server's lastOrdinal and succeed");
      assert.equal(appendRequests, 2, "one gap reject + one successful resend — no retry-loop attempts wasted on either");
    } finally {
      await close();
    }
  });
});

// Point 1 (coordinator review of ca1076f0): a retryable status must NOT fall into the legacy
// full-snapshot fallback just because a small attempt COUNT was reached — it must keep retrying
// the SAME increment route until the shared deadline itself runs out. This server fails more
// times than the old hardcoded cap (4) ever allowed, then recovers — proving the fix landed
// entirely via the correct route, with the legacy endpoint never touched at all.
test("append: 5 failures then 200 — retry now exceeds the OLD 4-attempt cap; still lands via the append route, legacy never touched", async () => {
  await withIsolatedHome(async () => {
    let appendRequests = 0;
    let legacyRequests = 0;
    const FAILURES_BEYOND_OLD_CAP = 5; // strictly more than the retired MAX_RETRY_ATTEMPTS (4)
    const { baseUrl, close } = await startFakeServer((req, res) => {
      if (req.url?.includes("/append")) {
        appendRequests++;
        if (appendRequests <= FAILURES_BEYOND_OLD_CAP) {
          res.writeHead(503, { "Content-Type": "application/json" }).end(JSON.stringify({ error: "restarting" }));
          return;
        }
        res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify({ lastOrdinal: MSGS.length }));
        return;
      }
      legacyRequests++;
      res.writeHead(500).end();
    });
    try {
      const result = await pushTranscript(target(baseUrl), MSGS, null);
      assert.equal(result, MSGS.length, "must still land the turn — the deadline had plenty of room left for attempt 6");
      assert.equal(appendRequests, FAILURES_BEYOND_OLD_CAP + 1, "all retries stayed on the increment route");
      assert.equal(legacyRequests, 0, "the legacy full-snapshot route must NEVER be touched for a retryable status — that was the exact regression under review");
    } finally {
      await close();
    }
  });
});

// Point 1 + Point 2 together, end to end: a server that NEVER recovers. The old design gave up
// on the increment route after 4 attempts (~1-2s) and spent the remaining ~8s of budget sending
// full snapshots to the same struggling server instead — exactly the "ухудшение, а не деградация"
// the coordinator called out. The fix must spend the WHOLE shared deadline retrying the CORRECT
// route, and when that deadline is finally gone, the legacy phase must send NOTHING and say so
// honestly (not fabricate a "network failure" that never happened — Point 2).
//
// WHAT IS ASSERTED HERE IS NOT HOW LONG THE TEST TOOK. Two wall-clock bounds used to flank this
// (`elapsed >= 9000`, `elapsed < 11500`) and they were the one part of the file that measured the
// box instead of the code: under a loaded full gate the same test failed on a log line its own
// retry loop had raced past (observation append-retry-503-deadline-test-flaky-under-load). The
// deadline's EFFECT is what matters and it is visible without a stopwatch:
//   * `appendRequests >= 5` — the loop went past the retired 4-attempt cap at all, which a
//     count-bounded retry cannot do (the exact count is not asserted: how many attempts fit is a
//     property of how fast the box answered, and asserting it was the wall clock's job);
//   * `legacyRequests === 0` — the shared deadline, not a count, is what ended it, and the
//     legacy full-snapshot route was never even reached (Point 1);
//   * wire.log's "retry budget exhausted after the increment phase's N attempt(s); legacy
//     fallback could not attempt any request" — a diagnosis that can ONLY be produced by the
//     deadline path, with its attempt count cross-checked against what the server actually saw.
test("append: server always 503 (both routes) — the increment route retries for the WHOLE deadline, legacy is never actually called, and wire.log names the real diagnosis", async () => {
  await withIsolatedHome(async (home) => {
    // Backoff (append.ts's backoffDelayMs) is jittered 0.5x-1.5x per attempt precisely so that a
    // BATCH of parallel sessions does not retry in lockstep — it is not something this test wants
    // to vary. Pinning it makes the retry SEQUENCE a property of the code rather than a draw: at
    // the maximum jitter the loop's last sleep is always the one capped to the remaining budget,
    // so it exits through the deadline check with the final 503 in hand. Left free, a draw can
    // leave a sliver-sized final attempt (a few ms), and whether a loopback round trip finishes
    // inside a sliver is exactly the load-dependent race that made this test red under the gate.
    const random = mock.method(Math, "random", () => 1);
    let appendRequests = 0;
    let legacyRequests = 0;
    const { baseUrl, close } = await startFakeServer((req, res) => {
      if (req.url?.includes("/append")) appendRequests++;
      else legacyRequests++;
      res.writeHead(503, { "Content-Type": "application/json" }).end(JSON.stringify({ error: "restarting" }));
    });
    try {
      const result = await pushTranscript(target(baseUrl), MSGS, null);

      assert.equal(result, null, "an always-503 server must still ultimately fail best-effort (null), never throw");
      assert.ok(appendRequests >= 5, `must retry past the OLD 4-attempt cap while budget remains, got only ${appendRequests} append attempts`);
      assert.equal(legacyRequests, 0, "Point 1: a retryable status (503) must NEVER fall into the legacy full-snapshot route — no full snapshot may ever be sent to a server that just said it's busy");

      const log = wireLogText(home);
      const match = log.match(
        /pushTranscript legacy fallback for fake-project\/fake-session \(agent=claude-code\) — retry budget exhausted after the increment phase's (\d+) attempt\(s\); legacy fallback could not attempt any request — session NOT persisted this turn/,
      );
      assert.ok(match, `Point 2: wire.log must name the REAL diagnosis (budget exhausted, no request attempted), not a fabricated network failure, got:\n${log}`);
      assert.equal(Number(match![1]), appendRequests, "the logged attempt count must match what the increment phase actually did");
      assert.ok(!log.includes("network failure"), "Point 2: there was no network failure — every request got a real 503 response — the log must not claim otherwise");
    } finally {
      random.mock.restore();
      await close();
    }
  });
});
