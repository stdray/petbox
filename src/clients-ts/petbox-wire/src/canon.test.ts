// Unit coverage for canon.ts's consumption of the curated-empty leg (Version 0) — cards
// canon-invisible-and-unfed (item 1, the KIT side) and canon-banner-empty-notice-unlabelled
// (the empty notice must be attributed to its OWN leg, never glued unheaded onto the end of a
// populated section).
//
// Before canon-invisible-and-unfed, canon.ts's partBody()/buildBlock() treated ANY non-blank
// body as real content — so once the server started answering an empty leg with
// `{ body: "...", version: 0 }` instead of null, this kit would have wrapped that nudge in a
// "### Project (name)" heading exactly like real curated text, and (worse) cached it under the
// same cache file the REAL canon uses, risking it resurfacing stale after a later curation.
//
// Before canon-banner-empty-notice-unlabelled, the fix for THAT wrapped the empty notice with no
// heading at all, gluing it onto the tail of the block — so a populated project section directly
// followed by an unheaded "canon is empty" line read as a claim about the WHOLE canon rather than
// just the empty workspace leg. The server ALSO used to carry that notice's prose in Body; it now
// sends Body="" for an empty leg (MemoryApi.cs's ReadCanonAsync) and the kit synthesizes the
// human-readable text itself (EMPTY_CANON_TEXT in canon.ts), attributed under a heading naming
// the specific leg ("### Project (name) — empty" / "### Workspace — empty").
//
// All exercised here against an in-process fake HTTP server, never a spawned child process
// (fetchCanonBlock is a plain async function — no need to pay the subprocess cost or risk the
// documented spawnSync-vs-in-process-server deadlock some other tests in this kit avoid by using
// async `spawn` instead).
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

// Pinned verbatim against canon.ts's own EMPTY_CANON_TEXT (not exported — this is the kit's
// OWN rendered text, no longer anything the server sends; see that file's comment on why the
// server-side EmptyCanonMarker constant was retired). A wording drift here should break a test,
// not slip through silently.
const EMPTY_CANON_TEXT = "canon is empty — curate with memory_upsert (store `canon`, key `index`, budget 10k)";

// What the server actually sends today for an empty leg (MemoryApi.cs's ReadCanonAsync): Body
// is "", Version is 0. classification must go by Version alone — see the next test for a probe
// that an OLDER server (still sending prose in Body at Version 0) degrades identically.
const EMPTY_LEG = { body: "", updatedAt: new Date().toISOString(), version: 0 };

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

test("AFTER: a queried-but-empty leg (version 0) renders under its OWN 'Project ... — empty' heading, attributed to that leg", async () => {
  const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: EMPTY_LEG, workspace: null }));
  try {
    const block = await fetchCanonBlock(resolvedFor(baseUrl));
    assert.notEqual(block, null, "a version-0 leg must produce a canon block, not silence");
    const text = block as string;

    const noticeLines = text.split("\n").filter((l) => l === EMPTY_CANON_TEXT);
    assert.equal(noticeLines.length, 1, `expected exactly one empty-notice line, got:\n${text}`);

    assert.ok(
      text.includes(`### Project (${resolvedFor(baseUrl).project}) — empty\n\n${EMPTY_CANON_TEXT}`),
      `the empty-canon notice must be attributed to the SPECIFIC leg via its own heading, got:\n${text}`,
    );
  } finally {
    await close();
  }
});

test("AFTER: project has real content, workspace is empty — content shown normally under its heading, empty leg gets its OWN heading (never glued unheaded onto the content section)", async () => {
  const { baseUrl, close } = await startFakeCanonServer(
    jsonHandler({
      project: { body: "- fact one\n- fact two", updatedAt: new Date().toISOString(), version: 3 },
      workspace: EMPTY_LEG,
    }),
  );
  try {
    const block = (await fetchCanonBlock(resolvedFor(baseUrl))) as string;
    assert.ok(block.includes("### Project (fake-project)\n\n- fact one"), "real content keeps its heading");
    assert.ok(
      block.includes(`### Workspace — empty\n\n${EMPTY_CANON_TEXT}`),
      `the empty workspace leg must carry its OWN 'Workspace — empty' heading, not a bare instruction line ` +
        `glued onto the tail of the project section (card canon-banner-empty-notice-unlabelled), got:\n${block}`,
    );
    assert.ok(
      !block.includes(`- fact two\n\n${EMPTY_CANON_TEXT}`),
      "the empty notice must never sit directly after populated content with no separating heading",
    );
  } finally {
    await close();
  }
});

test("AFTER: both legs empty — EACH gets its own attributed heading (never deduped into one unattributed line)", async () => {
  const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: EMPTY_LEG, workspace: EMPTY_LEG }));
  try {
    const block = (await fetchCanonBlock(resolvedFor(baseUrl))) as string;
    assert.ok(
      block.includes(`### Project (fake-project) — empty\n\n${EMPTY_CANON_TEXT}`),
      `project leg must be attributed, got:\n${block}`,
    );
    assert.ok(
      block.includes(`### Workspace — empty\n\n${EMPTY_CANON_TEXT}`),
      `workspace leg must ALSO be attributed (not silently collapsed away — "empty" is always about ` +
        `a specific part, per the card's acceptance criteria), got:\n${block}`,
    );
    const occurrences = block.split(EMPTY_CANON_TEXT).length - 1;
    assert.equal(occurrences, 2, `both legs being empty must show TWO attributed notices, got ${occurrences} in:\n${block}`);
  } finally {
    await close();
  }
});

test("AFTER: an older server still sending prose in Body at Version 0 is classified by VERSION, not Body text — the leg is still 'empty', the server's prose is discarded", async () => {
  const { baseUrl, close } = await startFakeCanonServer(
    jsonHandler({
      project: { body: "canon is empty — curate with memory_upsert (store `canon`, key `index`, budget 10k)", updatedAt: new Date().toISOString(), version: 0 },
      workspace: null,
    }),
  );
  try {
    const block = (await fetchCanonBlock(resolvedFor(baseUrl))) as string;
    assert.ok(
      block.includes(`### Project (${resolvedFor(baseUrl).project}) — empty\n\n${EMPTY_CANON_TEXT}`),
      `an older server's Version-0 leg must still classify as empty and render the kit's OWN text ` +
        `under its own heading regardless of what Body carried, got:\n${block}`,
    );
  } finally {
    await close();
  }
});

test("cache stickiness: an empty-leg-only fetch is NEVER written to the offline cache", async () => {
  await withIsolatedHome(async (home) => {
    const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: EMPTY_LEG, workspace: null }));
    try {
      const block = await fetchCanonBlock(resolvedFor(baseUrl));
      assert.notEqual(block, null, "empty notice still shown live this session");
      assert.throws(
        () => readFileSync(cacheFile(home, "fake-project"), "utf8"),
        "an empty-leg-only response must not create a cache file at all",
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

test("cache stickiness: the empty leg never resurrects itself from cache after being (correctly) never cached", async () => {
  await withIsolatedHome(async (home) => {
    const project = "fake-project";
    const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: EMPTY_LEG, workspace: null }));
    try {
      await fetchCanonBlock(resolvedFor(baseUrl, project));
    } finally {
      await close();
    }
    // Server now unreachable. Because the empty leg was never cached, there is nothing to fall
    // back to — the hook must show NOTHING this session, not a stale "canon is empty" claim
    // that could by now be false (the owner may have curated it in the meantime).
    const dead = resolvedFor("http://127.0.0.1:1", project);
    const block = await fetchCanonBlock(dead, { timeoutMs: 500 });
    assert.equal(block, null, "no cache should exist for an empty-leg-only response, so offline fallback must be null");
    assert.throws(() => readFileSync(cacheFile(home, project), "utf8"));
  });
});

// canon-degrade-by-legs-not-all-or-nothing, end to end: the block here is rendered by canon.ts's
// OWN buildBlock (via a real fetch against the fake server), not hand-assembled — so this proves
// the budget ladder can actually find the workspace boundary in the text the renderer produces,
// which a unit test over a synthetic string cannot.
test("end-to-end: an over-budget REAL two-leg canon block sheds only the workspace leg, project canon survives", async () => {
  const projectBody = "- project fact\n".repeat(60);
  const workspaceBody = "- workspace fact\n".repeat(120);
  const { baseUrl, close } = await startFakeCanonServer(
    jsonHandler({
      project: { body: projectBody, updatedAt: new Date().toISOString(), version: 3 },
      workspace: { body: workspaceBody, updatedAt: new Date().toISOString(), version: 4 },
    }),
  );
  try {
    await withIsolatedHome(async () => {
      const canon = await fetchCanonBlock(resolvedFor(baseUrl));
      assert.ok(canon, "fixture assumption: the fake server yields a two-leg block");
      assert.ok(canon!.includes("### Workspace"), "fixture assumption: the block really has a workspace leg");

      const protocol = buildProtocol("fake-project", mcpPetboxTool, {
        source: "startup",
        harness: "claude-code",
        definition: DEFAULT_AGENT_DEFINITION,
      });
      // A budget that fits the protocol and the project leg but NOT the workspace leg: the exact
      // shape of the incident this ladder exists for.
      const projectOnlyBytes = Buffer.byteLength(canon!.slice(0, canon!.lastIndexOf("\n\n### Workspace")), "utf8");
      const budget = Buffer.byteLength(protocol, "utf8") + 2 + projectOnlyBytes;

      const banner = assembleSessionBanner(protocol, canon, budget);
      assert.equal(banner.canonLegs, "project-only");
      assert.ok(banner.text.includes("### Project (fake-project)"), "the project leg must still be delivered");
      assert.ok(banner.text.includes("- project fact"), "and with its actual curated body, not just the heading");
      assert.ok(!banner.text.includes("### Workspace"), "the workspace leg is what pays for the overage");
      assert.ok(!banner.text.includes("- workspace fact"));
      assert.ok(banner.totalBytes <= budget);
    });
  } finally {
    await close();
  }
});

test("end-to-end: assembleSessionBanner with the new attributed-heading shape ships exactly one empty-canon line, comfortably inside budget", async () => {
  const { baseUrl, close } = await startFakeCanonServer(jsonHandler({ project: EMPTY_LEG, workspace: null }));
  try {
    const canon = await fetchCanonBlock(resolvedFor(baseUrl));
    const protocol = buildProtocol("fake-project", mcpPetboxTool, {
      source: "startup",
      harness: "claude-code",
      definition: DEFAULT_AGENT_DEFINITION,
    });
    const banner = assembleSessionBanner(protocol, canon);

    assert.equal(banner.overBudget, false, "protocol + a ~90-byte empty-notice line must never be over budget");
    assert.equal(banner.canonIncluded, true, "the tiny empty-notice block always fits alongside the protocol block");

    const noticeLines = banner.text.split("\n").filter((l) => l === EMPTY_CANON_TEXT);
    assert.equal(
      noticeLines.length,
      1,
      `the ASSEMBLED banner (what the harness actually inlines) must carry exactly ONE line about ` +
        `the empty canon, got ${noticeLines.length} in:\n${banner.text}`,
    );
    assert.ok(
      banner.text.includes(`### Project (fake-project) — empty\n\n${EMPTY_CANON_TEXT}`),
      "the notice must be attributed to the specific leg via its own '... — empty' heading, " +
        `never a bare instruction line unattached to any section, got:\n${banner.text}`,
    );
  } finally {
    await close();
  }
});
