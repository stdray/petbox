// Unit coverage for canon.ts's consumption of the empty-canon marker (card
// canon-invisible-and-unfed, item 1 — the KIT side).
//
// The card's acceptance criterion is about the INJECT, not the HTTP response: "empty canon ->
// inject adds ONE line" that reads as an instruction to the agent, not as a curated fact about
// the project. Before this fix, canon.ts's partBody()/buildBlock() treated ANY non-blank body
// as real content — so even once the server started answering an empty leg with
// `{ body: EmptyCanonMarker, version: 0 }` instead of null, this kit would have wrapped that
// nudge in a "### Project (name)" heading exactly like real curated text, and (worse) cached it
// under the same cache file the REAL canon uses, risking it resurfacing stale after a later
// curation. Both are exercised here against an in-process fake HTTP server, never a spawned
// child process (fetchCanonBlock is a plain async function — no need to pay the subprocess
// cost or risk the documented spawnSync-vs-in-process-server deadlock some other tests in this
// kit avoid by using async `spawn` instead).
//
// Run: node --test src/canon.test.ts

import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import http from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { fetchCanonBlock } from "./canon.ts";
import { buildProtocol, mcpPetboxTool } from "./protocol.ts";
import { DEFAULT_AGENT_DEFINITION } from "./agent-definition.ts";
import { assembleSessionBanner } from "./session-budget.ts";
import type { ResolvedProject } from "./registry.ts";

// Pinned verbatim against src/PetBox.Web/Memory/MemoryApi.cs's EmptyCanonMarker (also pinned
// server-side in tests/PetBox.Tests/Web/MemoryCanonApiTests.cs) — a wording drift on either
// side should break a test, not slip through silently.
const EMPTY_CANON_MARKER = "canon is empty — curate with memory_upsert (store `canon`, key `index`, budget 10k)";

type Handler = (req: http.IncomingMessage, res: http.ServerResponse) => void;

function startFakeCanonServer(handler: Handler): Promise<{ baseUrl: string; close: () => Promise<void> }> {
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

function jsonHandler(body: unknown): Handler {
  return (_req, res) => {
    res.writeHead(200, { "Content-Type": "application/json" }).end(JSON.stringify(body));
  };
}

function resolvedFor(baseUrl: string, project = "fake-project"): ResolvedProject {
  return { project, apiKey: "fake-key", baseUrl, envVar: "FAKE_CANON_TEST_KEY" };
}

// Isolate ~/.petbox/cache per test so cache-stickiness assertions never see another test's
// (or a real dev machine's) cache file. Mutates process.env for the span of one test only —
// node:test runs this file's tests sequentially by default, so this is safe.
function restoreEnv(key: string, prev: string | undefined): void {
  if (prev === undefined) delete process.env[key];
  else process.env[key] = prev;
}

function withIsolatedHome<T>(fn: (home: string) => Promise<T>): Promise<T> {
  const home = mkdtempSync(join(tmpdir(), "petbox-canon-test-"));
  const prevHome = process.env["HOME"];
  const prevProfile = process.env["USERPROFILE"];
  process.env["HOME"] = home;
  process.env["USERPROFILE"] = home; // os.homedir() reads USERPROFILE on win32, HOME on POSIX
  return fn(home).finally(() => {
    restoreEnv("HOME", prevHome);
    restoreEnv("USERPROFILE", prevProfile);
    rmSync(home, { recursive: true, force: true });
  });
}

function cacheFile(home: string, project: string): string {
  return join(home, ".petbox", "cache", `${project}.canon.md`);
}

test("BEFORE (pre-fix server contract): a genuinely empty leg is null, not a marker — no block, unchanged behavior", async () => {
  const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: null, workspace: null }));
  try {
    const block = await fetchCanonBlock(resolvedFor(baseUrl));
    assert.equal(block, null, "pre-fix null/null must still degrade to no canon block at all");
  } finally {
    await close();
  }
});

test("AFTER: a queried-but-empty leg (version 0) renders as exactly ONE marker line, not a '### Project' fact", async () => {
  const { baseUrl, close } = await startFakeCanonServer(
    jsonHandler({ project: { body: EMPTY_CANON_MARKER, updatedAt: new Date().toISOString(), version: 0 }, workspace: null }),
  );
  try {
    const block = await fetchCanonBlock(resolvedFor(baseUrl));
    assert.notEqual(block, null, "a version-0 marker leg must produce a canon block, not silence");
    const text = block as string;

    const markerLines = text.split("\n").filter((l) => l === EMPTY_CANON_MARKER);
    assert.equal(markerLines.length, 1, `expected exactly one marker line, got:\n${text}`);

    assert.ok(
      !text.includes(`### Project (${resolvedFor(baseUrl).project})`),
      "the empty-canon nudge must NOT be wrapped in a '### Project (name)' heading — that framing " +
        "would make an instruction read as a curated fact about the named project",
    );
  } finally {
    await close();
  }
});

test("AFTER: project has real content, workspace is the empty marker — content shown normally, marker still exactly one line", async () => {
  const { baseUrl, close } = await startFakeCanonServer(
    jsonHandler({
      project: { body: "- fact one\n- fact two", updatedAt: new Date().toISOString(), version: 3 },
      workspace: { body: EMPTY_CANON_MARKER, updatedAt: new Date().toISOString(), version: 0 },
    }),
  );
  try {
    const block = (await fetchCanonBlock(resolvedFor(baseUrl))) as string;
    assert.ok(block.includes("### Project (fake-project)"), "real content keeps its heading");
    assert.ok(block.includes("- fact one"), "real content body must survive");
    const markerLines = block.split("\n").filter((l) => l === EMPTY_CANON_MARKER);
    assert.equal(markerLines.length, 1, `expected exactly one marker line alongside real content, got:\n${block}`);
    assert.ok(!block.includes("### Workspace\n\ncanon is empty"), "the empty workspace leg must not be headed like a fact");
  } finally {
    await close();
  }
});

test("AFTER: both legs empty — identical marker text is DEDUPED to one line, not printed twice", async () => {
  const marker = { body: EMPTY_CANON_MARKER, updatedAt: new Date().toISOString(), version: 0 };
  const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: marker, workspace: marker }));
  try {
    const block = (await fetchCanonBlock(resolvedFor(baseUrl))) as string;
    const occurrences = block.split(EMPTY_CANON_MARKER).length - 1;
    assert.equal(occurrences, 1, `both legs sharing the same nudge text must collapse to ONE line, got ${occurrences} in:\n${block}`);
  } finally {
    await close();
  }
});

test("cache stickiness: an empty-marker fetch is NEVER written to the offline cache", async () => {
  await withIsolatedHome(async (home) => {
    const { baseUrl, close } = await startFakeCanonServer(
      jsonHandler({ project: { body: EMPTY_CANON_MARKER, updatedAt: new Date().toISOString(), version: 0 }, workspace: null }),
    );
    try {
      const block = await fetchCanonBlock(resolvedFor(baseUrl));
      assert.notEqual(block, null, "marker still shown live this session");
      assert.throws(
        () => readFileSync(cacheFile(home, "fake-project"), "utf8"),
        "an empty-marker-only response must not create a cache file at all",
      );
    } finally {
      await close();
    }
  });
});

test("cache stickiness: a real-content fetch IS cached and survives a later network failure (ordinary staleness, unaffected)", async () => {
  await withIsolatedHome(async (home) => {
    const project = "fake-project";
    const realBody = "- durable fact";
    const { baseUrl: goodUrl, close: closeGood } = await startFakeCanonServer(
      jsonHandler({ project: { body: realBody, updatedAt: new Date().toISOString(), version: 4 }, workspace: null }),
    );
    try {
      const first = await fetchCanonBlock(resolvedFor(goodUrl, project));
      assert.ok(first?.includes(realBody), "real content must be returned live");
      const cached = readFileSync(cacheFile(home, project), "utf8");
      assert.ok(cached.includes(realBody), "real content must be cached");
    } finally {
      await closeGood();
    }

    // Now the server is unreachable — the cached REAL content must come back, stale-prefixed,
    // exactly as before this fix (ordinary content staleness is not what this card changes).
    const dead = resolvedFor("http://127.0.0.1:1", project); // nothing listens here
    const second = await fetchCanonBlock(dead, { timeoutMs: 500 });
    assert.ok(second?.includes(realBody), "a real cached canon must still survive a network outage");
    assert.ok(second?.includes("may be stale"), "the stale-cache prefix must still be present");
  });
});

test("cache stickiness: the empty marker never resurrects itself from cache after being (correctly) never cached", async () => {
  await withIsolatedHome(async (home) => {
    const project = "fake-project";
    const { baseUrl, close } = await startFakeCanonServer(
      jsonHandler({ project: { body: EMPTY_CANON_MARKER, updatedAt: new Date().toISOString(), version: 0 }, workspace: null }),
    );
    try {
      await fetchCanonBlock(resolvedFor(baseUrl, project));
    } finally {
      await close();
    }
    // Server now unreachable. Because the marker was never cached, there is nothing to fall
    // back to — the hook must show NOTHING this session, not a stale "canon is empty" claim
    // that could by now be false (the owner may have curated it in the meantime).
    const dead = resolvedFor("http://127.0.0.1:1", project);
    const block = await fetchCanonBlock(dead, { timeoutMs: 500 });
    assert.equal(block, null, "no cache should exist for a marker-only leg, so offline fallback must be null");
    assert.throws(() => readFileSync(cacheFile(home, project), "utf8"));
  });
});

test("end-to-end: assembleSessionBanner with the new marker shape ships exactly one empty-canon line, comfortably inside budget", async () => {
  const { baseUrl, close } = await startFakeCanonServer(
    jsonHandler({ project: { body: EMPTY_CANON_MARKER, updatedAt: new Date().toISOString(), version: 0 }, workspace: null }),
  );
  try {
    const canon = await fetchCanonBlock(resolvedFor(baseUrl));
    const protocol = buildProtocol("fake-project", mcpPetboxTool, {
      source: "startup",
      harness: "claude-code",
      definition: DEFAULT_AGENT_DEFINITION,
    });
    const banner = assembleSessionBanner(protocol, canon);

    assert.equal(banner.overBudget, false, "protocol + a ~90-byte marker line must never be over budget");
    assert.equal(banner.canonIncluded, true, "the tiny marker block always fits alongside the protocol block");

    const markerLines = banner.text.split("\n").filter((l) => l === EMPTY_CANON_MARKER);
    assert.equal(
      markerLines.length,
      1,
      `the ASSEMBLED banner (what the harness actually inlines) must carry exactly ONE line about ` +
        `the empty canon, got ${markerLines.length} in:\n${banner.text}`,
    );
    assert.ok(
      markerLines[0]?.startsWith("canon is empty"),
      "the line must read as an instruction ('canon is empty — curate...'), not as a fact framed by a project heading",
    );
  } finally {
    await close();
  }
});
