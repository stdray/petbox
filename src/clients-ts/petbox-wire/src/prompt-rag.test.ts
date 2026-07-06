// Unit tests for the prompt-RAG hook's exact-match core — pure, no network. The MCP lookup is
// stubbed via the injectable Resolver, so these assert the deterministic contract directly:
// exact-join only, pointers only, silence when nothing matches.
//
// Run: node --test src/prompt-rag.test.ts   (Node >= 23.6 native TS type-stripping; no build step)

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  auditToClefLine,
  buildAuditRecord,
  buildInjection,
  buildInjectionDetailed,
  buildInjectionForProject,
  extractCandidates,
  renderInjection,
  tolerancesOf,
  type Resolver,
  type TaskHit,
} from "./prompt-rag.ts";

// ---- extractCandidates: deterministic identifier extraction --------------------------------

test("extractCandidates pulls hyphenated slugs and 32-hex NodeIds, skips plain words", () => {
  const c = extractCandidates("please look at telemetry-wire-toggle and node 3f36d5ccbbec4019a73d761ec9e29818 now");
  assert.ok(c.includes("telemetry-wire-toggle"), "hyphenated slug is a candidate");
  assert.ok(c.includes("3f36d5ccbbec4019a73d761ec9e29818"), "32-hex NodeId is a candidate");
  assert.ok(!c.includes("please"), "plain prose words are not candidates");
  assert.ok(!c.includes("now"));
});

test("extractCandidates requires a hyphen (single words are NOT candidates)", () => {
  assert.deepEqual(extractCandidates("fix the telemetry and the toggle"), []);
});

test("extractCandidates dedupes and caps at 8", () => {
  assert.deepEqual(extractCandidates("a-b a-b a-b"), ["a-b"]);
  const many = Array.from({ length: 20 }, (_, i) => `slug-${i}`).join(" ");
  assert.equal(extractCandidates(many).length, 8);
});

test("extractCandidates is empty for empty / non-string input", () => {
  assert.deepEqual(extractCandidates(""), []);
  assert.deepEqual(extractCandidates(undefined as unknown as string), []);
});

// ---- renderInjection: pointers only, never bodies ------------------------------------------

test("renderInjection emits a pointer line with an expand command, no body", () => {
  const hit: TaskHit = {
    key: "prompt-rag-hook",
    board: "work",
    status: "InProgress",
    type: "feature",
    title: "prompt-RAG hook",
    nodeId: "3f36d5ccbbec4019a73d761ec9e29818",
  };
  const out = renderInjection([hit]);
  assert.match(out, /work\/prompt-rag-hook/);
  assert.match(out, /\[InProgress feature\]/);
  assert.match(out, /tasks_node_get\(board="work", node="prompt-rag-hook"\)/);
});

test("renderInjection returns empty string for no hits (→ inject nothing)", () => {
  assert.equal(renderInjection([]), "");
});

// ---- buildInjection: exact-join orchestration ----------------------------------------------

const KNOWN: Record<string, TaskHit> = {
  "telemetry-wire-toggle": {
    key: "telemetry-wire-toggle",
    board: "work",
    status: "InProgress",
    type: "feature",
    title: "telemetry toggle",
    nodeId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  },
};
const stubResolver: Resolver = async (tok) => KNOWN[tok] ?? null;

test("buildInjection: EXACT match → a pointer is produced", async () => {
  const out = await buildInjection("work on telemetry-wire-toggle please", stubResolver);
  assert.match(out, /work\/telemetry-wire-toggle/);
  assert.match(out, /expand: mcp__petbox__tasks_node_get/);
});

test("buildInjection: NO exact match → empty (silent)", async () => {
  const out = await buildInjection("work on some-nonexistent-slug please", stubResolver);
  assert.equal(out, "");
});

test("buildInjection: no identifier tokens at all → empty, resolver never called", async () => {
  let calls = 0;
  const counting: Resolver = async (t) => {
    calls++;
    return stubResolver(t);
  };
  const out = await buildInjection("just some ordinary words here", counting);
  assert.equal(out, "");
  assert.equal(calls, 0, "no candidates → zero lookups (zero network)");
});

test("buildInjection: same node named twice (slug + nodeId) → deduped to one pointer", async () => {
  const both: Resolver = async (tok) => {
    if (tok === "telemetry-wire-toggle" || tok === "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") {
      return KNOWN["telemetry-wire-toggle"];
    }
    return null;
  };
  const out = await buildInjection(
    "telemetry-wire-toggle aka aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    both,
  );
  const occurrences = out.split("telemetry-wire-toggle").length - 1;
  // The slug appears in both the heading and the expand command of a single pointer line (2x),
  // but NOT duplicated across two lines.
  assert.equal(out.split("\n").filter((l) => l.startsWith("- ")).length, 1, "one pointer line only");
  assert.ok(occurrences >= 1);
});

test("buildInjection: a resolver that throws on one token doesn't abort the rest", async () => {
  const flaky: Resolver = async (tok) => {
    if (tok === "bad-token") throw new Error("boom");
    return KNOWN[tok] ?? null;
  };
  const out = await buildInjection("bad-token and telemetry-wire-toggle", flaky);
  assert.match(out, /telemetry-wire-toggle/, "the good match still lands despite the thrown lookup");
});

// ---- tolerances: cap + requireHyphen actually read from config -----------------------------

test("extractCandidates: cap param bounds the candidate count", () => {
  const many = Array.from({ length: 20 }, (_, i) => `slug-${i}`).join(" ");
  assert.equal(extractCandidates(many, 3).length, 3, "cap=3 keeps only 3");
  assert.equal(extractCandidates(many, 1).length, 1, "cap=1 keeps only 1");
  // A non-positive / bogus cap falls back to the default (8), never 0 or negative.
  assert.equal(extractCandidates(many, 0).length, 8, "cap=0 → default");
  assert.equal(extractCandidates(many, -5).length, 8, "negative cap → default");
});

test("extractCandidates: requireHyphen=false admits single-word tokens; true rejects them", () => {
  assert.deepEqual(extractCandidates("fix the toggle now", 8, true), [], "hyphen required → no single words");
  const relaxed = extractCandidates("fix the toggle now", 8, false);
  assert.ok(relaxed.includes("toggle"), "relaxed mode accepts a plain word");
  assert.ok(relaxed.includes("fix") && relaxed.includes("now"));
});

test("tolerancesOf: fills defaults, honors overrides", () => {
  assert.deepEqual(tolerancesOf(undefined), { cap: 8, requireHyphen: true });
  assert.deepEqual(tolerancesOf({ enabled: true }), { cap: 8, requireHyphen: true });
  assert.deepEqual(tolerancesOf({ enabled: true, cap: 3, requireHyphen: false }), { cap: 3, requireHyphen: false });
});

// ---- buildInjectionForProject: per-project gate (enabled) + tolerances ---------------------

test("gate: disabled config → empty output EVEN WITH a matching identifier (no injection)", async () => {
  const out = await buildInjectionForProject("work on telemetry-wire-toggle", { enabled: false }, stubResolver);
  assert.equal(out, "", "enabled:false suppresses the pointer entirely");
});

test("gate: absent config → empty output (back-compat: unconfigured project stays silent)", async () => {
  const out = await buildInjectionForProject("work on telemetry-wire-toggle", undefined, stubResolver);
  assert.equal(out, "");
});

test("gate: enabled config → a pointer is produced for a matching identifier", async () => {
  const out = await buildInjectionForProject("work on telemetry-wire-toggle", { enabled: true }, stubResolver);
  assert.match(out, /work\/telemetry-wire-toggle/);
  assert.match(out, /expand: mcp__petbox__tasks_node_get/);
});

test("gate: enabled but disabled resolver never runs when config is disabled (zero network)", async () => {
  let calls = 0;
  const counting: Resolver = async (t) => {
    calls++;
    return stubResolver(t);
  };
  await buildInjectionForProject("telemetry-wire-toggle", { enabled: false }, counting);
  assert.equal(calls, 0, "disabled → no resolver calls (no network)");
});

test("tolerances flow through the gate: cap from config bounds lookups", async () => {
  let calls = 0;
  const counting: Resolver = async (t) => {
    calls++;
    return KNOWN[t] ?? null;
  };
  // Two distinct identifiers in the prompt, but cap:1 → only the first is ever looked up.
  await buildInjectionForProject(
    "telemetry-wire-toggle and another-real-slug",
    { enabled: true, cap: 1 },
    counting,
  );
  assert.equal(calls, 1, "cap:1 from config → exactly one lookup");
});

test("tolerances flow through the gate: requireHyphen=false lets a single-word key match", async () => {
  const single: Resolver = async (t) => (t === "toggle" ? KNOWN["telemetry-wire-toggle"] : null);
  const off = await buildInjectionForProject("please check toggle", { enabled: true, requireHyphen: true }, single);
  assert.equal(off, "", "default (hyphen required) → 'toggle' is not even a candidate");
  const on = await buildInjectionForProject("please check toggle", { enabled: true, requireHyphen: false }, single);
  assert.match(on, /telemetry-wire-toggle/, "requireHyphen:false → single word resolves to a pointer");
});

// ---- buildInjectionDetailed: text stays identical, exposes audit signals -------------------

test("buildInjectionDetailed: text equals buildInjection, plus hits + candidateCount", async () => {
  const prompt = "work on telemetry-wire-toggle and no-such-slug please";
  const detailed = await buildInjectionDetailed(prompt, stubResolver);
  const text = await buildInjection(prompt, stubResolver);
  assert.equal(detailed.text, text, "text is byte-identical to buildInjection");
  assert.equal(detailed.candidateCount, 2, "both hyphenated identifiers are candidates");
  assert.equal(detailed.hits.length, 1, "only the real node resolves to a hit");
  assert.equal(detailed.hits[0].key, "telemetry-wire-toggle");
});

test("buildInjectionDetailed: no candidates → empty text, zero counts", async () => {
  const detailed = await buildInjectionDetailed("just ordinary words", stubResolver);
  assert.deepEqual(detailed, { text: "", hits: [], candidateCount: 0 });
});

// ---- buildAuditRecord: pure record builder -------------------------------------------------

const FIXED_NOW = new Date("2026-07-06T12:00:00.000Z");

test("buildAuditRecord: a match → injected=true, counts + matched board/key, promptLen, session", () => {
  const hit = KNOWN["telemetry-wire-toggle"];
  const rec = buildAuditRecord("prompt with telemetry-wire-toggle", 3, [hit], "sess-123", FIXED_NOW);
  assert.equal(rec.injected, true);
  assert.equal(rec.candidateCount, 3);
  assert.equal(rec.matchCount, 1);
  assert.deepEqual(rec.matched, ["work/telemetry-wire-toggle"]);
  assert.equal(rec.promptLen, "prompt with telemetry-wire-toggle".length);
  assert.equal(rec.sessionId, "sess-123");
  assert.equal(rec.timestamp, "2026-07-06T12:00:00.000Z");
});

test("buildAuditRecord: no matches → injected=false, matchCount 0, empty matched, no sessionId key", () => {
  const rec = buildAuditRecord("ordinary prompt", 0, [], undefined, FIXED_NOW);
  assert.equal(rec.injected, false);
  assert.equal(rec.matchCount, 0);
  assert.deepEqual(rec.matched, []);
  assert.equal(rec.promptLen, "ordinary prompt".length);
  assert.ok(!("sessionId" in rec), "absent session marker is omitted, not null");
});

test("buildAuditRecord: injected === (matchCount > 0), multiple matches dedupe-agnostic", () => {
  const a = KNOWN["telemetry-wire-toggle"];
  const b: TaskHit = { key: "other-node", board: "work", status: "Done", title: "other" };
  const rec = buildAuditRecord("p", 5, [a, b], undefined, FIXED_NOW);
  assert.equal(rec.matchCount, 2);
  assert.equal(rec.injected, rec.matchCount > 0);
  assert.deepEqual(rec.matched, ["work/telemetry-wire-toggle", "work/other-node"]);
});

// ---- auditToClefLine: one valid CLEF line, @t + structured props ---------------------------

test("auditToClefLine: emits a single-line valid CLEF JSON with @t and every field as a property", () => {
  const rec = buildAuditRecord("hello telemetry-wire-toggle", 2, [KNOWN["telemetry-wire-toggle"]], "sess-9", FIXED_NOW);
  const line = auditToClefLine(rec);
  assert.ok(!line.includes("\n"), "CLEF is a single NDJSON line");
  const o = JSON.parse(line);
  assert.equal(o["@t"], "2026-07-06T12:00:00.000Z", "@t is the ISO timestamp (the only required field)");
  assert.equal(typeof o["@m"], "string");
  assert.equal(o.injected, true);
  assert.equal(o.candidateCount, 2);
  assert.equal(o.matchCount, 1);
  assert.deepEqual(o.matched, ["work/telemetry-wire-toggle"]);
  assert.equal(o.promptLen, "hello telemetry-wire-toggle".length);
  assert.equal(o.sessionId, "sess-9");
});

test("auditToClefLine: omits sessionId when absent (no null property leaks)", () => {
  const o = JSON.parse(auditToClefLine(buildAuditRecord("p", 0, [], undefined, FIXED_NOW)));
  assert.ok(!("sessionId" in o), "no sessionId key when the marker was absent");
  assert.equal(o.injected, false);
  assert.deepEqual(o.matched, []);
});
