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
import { test } from "node:test";
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

test("append: server always 503 (both routes) — pushTranscript gives up in bounded time and logs the failure honestly, with an attempt count", async () => {
  await withIsolatedHome(async (home) => {
    let appendRequests = 0;
    let legacyRequests = 0;
    const { baseUrl, close } = await startFakeServer((req, res) => {
      if (req.url?.includes("/append")) appendRequests++;
      else legacyRequests++;
      res.writeHead(503, { "Content-Type": "application/json" }).end(JSON.stringify({ error: "restarting" }));
    });
    try {
      const start = Date.now();
      const result = await pushTranscript(target(baseUrl), MSGS, null);
      const elapsed = Date.now() - start;

      assert.equal(result, null, "an always-503 server must still ultimately fail best-effort (null), never throw");
      assert.ok(elapsed < 9000, `must give up comfortably inside the ~10s retry deadline via the attempt cap, took ${elapsed}ms`);
      assert.equal(appendRequests, 4, "increment phase must retry up to MAX_RETRY_ATTEMPTS, not once and not forever");
      assert.equal(legacyRequests, 4, "legacy fallback must ALSO retry up to the attempt cap (card item 4)");

      const log = wireLogText(home);
      assert.match(
        log,
        /pushTranscript legacy fallback for fake-project\/fake-session \(agent=claude-code\) got HTTP 503 after 4 attempt\(s\) — session NOT persisted this turn/,
        `wire.log must honestly report that retries WERE attempted and how many, not just that it failed, got:\n${log}`,
      );
    } finally {
      await close();
    }
  });
});
