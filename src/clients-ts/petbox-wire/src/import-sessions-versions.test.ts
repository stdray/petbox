// Unit test for import-sessions.ts's serverVersions() — card listing-keyset-memory-sessions
// (spec listing-tail-reachable): GET /api/sessions/{project} moved off "the whole archive in
// one response" onto KeysetCursor paging (?cursor=, nextCursor). The importer's own need is
// real ("compare every local candidate against the server's version") — this pins that the
// client walks `nextCursor` to the tail and merges every page into ONE map, rather than only
// ever reading page one (which would silently make every session past page 1 look "new" and
// re-push it, an upgrade-only-guard bypass, not a crash — much quieter and worse).
//
// Run: node --test src/import-sessions-versions.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import type { ResolvedProject } from "./registry.ts";
import { serverVersions } from "./import-sessions.ts";

const target: ResolvedProject = {
  project: "proj",
  apiKey: "yb_key_test",
  baseUrl: "https://petbox.example",
  envVar: "PETBOX_PROJ_API_KEY",
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
}

test("serverVersions: walks nextCursor across pages and merges into one map", async () => {
  const calls: string[] = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (async (input: Parameters<typeof fetch>[0]) => {
    const url = new URL(String(input));
    calls.push(url.search);
    const cursor = url.searchParams.get("cursor");
    if (!cursor) {
      return jsonResponse({
        sessions: [{ sessionId: "s1", version: 3 }],
        nextCursor: "page-2-token",
      });
    }
    assert.equal(cursor, "page-2-token");
    return jsonResponse({ sessions: [{ sessionId: "s2", version: 7 }], nextCursor: null });
  }) as typeof fetch;

  try {
    const versions = await serverVersions(target);
    assert.deepEqual(
      [...versions.entries()].sort(),
      [
        ["s1", 3],
        ["s2", 7],
      ],
      "both pages must be reached — a page-1-only read would silently lose s2",
    );
    assert.equal(calls.length, 2, "must stop as soon as nextCursor comes back null, not loop forever");
    assert.equal(calls[0], "", "the first call carries no cursor");
    assert.equal(calls[1], "?cursor=page-2-token", "the second call replays nextCursor verbatim");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("serverVersions: a single page (no nextCursor) makes exactly one call", async () => {
  const originalFetch = globalThis.fetch;
  let calls = 0;
  globalThis.fetch = (async () => {
    calls++;
    return jsonResponse({ sessions: [{ sessionId: "only", version: 1 }] }); // nextCursor omitted
  }) as typeof fetch;

  try {
    const versions = await serverVersions(target);
    assert.deepEqual([...versions.entries()], [["only", 1]]);
    assert.equal(calls, 1);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("serverVersions: a non-OK response throws with the status in the message", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (async () => new Response("nope", { status: 400 })) as typeof fetch;

  try {
    await assert.rejects(() => serverVersions(target), /400/);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
