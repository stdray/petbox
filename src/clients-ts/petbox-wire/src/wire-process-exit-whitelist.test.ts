// Structural guard against the libuv exit race, PACKAGE-WIDE: a hard `process.exit()` issued
// while libuv is still closing a socket left by a just-completed network round trip races that
// teardown on Windows —
//   Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 76
// — and the caller observes exit 127 instead of the code that call site actually named.
//
// History, because it is the whole argument for this file existing: the same defect was fixed
// four separate times (doctor, then status, then apply, then the six remaining sites in the full
// `wire` command plus import-sessions.ts). The first three fixes left no structural guard, which
// is exactly how it recurred. A BEHAVIORAL test cannot close the gap on its own — the race is
// timing-dependent, and apply-exit-race-libuv.test.ts's regression test passed 3/3 runs even
// against the UNFIXED code locally. A behavioral test proves the exit-CODE contract; only a
// structural one proves the shape is gone and stays gone.
//
// WHAT CHANGED IN THIS ROUND. Two things, and they are the point of the card:
//
//  1. This guard used to read exactly ONE file (wire.ts), which gave a false sense of a closed
//     class — raw exits lived outside it (import-sessions.ts). It now scans EVERY non-test .ts
//     in the package.
//
//  2. It used to TRACK risk ("risk-out-of-scope": same shape as the fixed bugs, tracked rather
//     than fixed because it sat outside the then-current card's scope). It now FORBIDS: the only
//     verdict is "safe", and every listed site carries a justification for why no network call
//     can precede it. There is deliberately no way to register a new risky exit — a call site
//     that would need one has a fix available instead (see below), so registering it would be
//     choosing to keep the bug.
//
// THE FIX, when this guard fails on a new call: end the run through wire-exit.ts, never
// `process.exit(code)`.
//   - `exitWith(code)`          — sets process.exitCode, unrefs handles still mid-close, and lets
//                                 Node exit naturally. Does not abort control flow; `return` after
//                                 it.
//   - `abortRun(code, message)` — when there is no clean `return` (deep inside a helper). Returns
//                                 `never`, so it cuts control flow exactly like process.exit did;
//                                 the entrypoint's `.catch` turns it back into `exitWith`.
// Both are SHORTER than the wrong way, which is the actual mechanism that keeps this closed.
//
// If a new call site genuinely cannot precede any network activity, add it to KNOWN_EXIT_SITES
// with an anchor and a justification that says WHY — verified by reading the enclosing function,
// never assumed.
//
// Note for whoever greps next: opencode-plugin.ts has neither `process.exit` nor `fetch`, and
// that is correct, not an oversight — it is a plugin module loaded INSIDE the opencode process,
// not a process of its own, so it has no exit of its own to get wrong.
//
// Run: node --test src/wire-process-exit-whitelist.test.ts

import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";

const SRC_DIR = import.meta.dirname;

// Every non-test .ts in the package's src/. Tests are excluded: they legitimately quote the
// pattern in prose and assertions, and a test process ending itself is not the shipped CLI
// contract this guard protects.
function packageSourceFiles(): readonly string[] {
  return readdirSync(SRC_DIR)
    .filter((f) => f.endsWith(".ts") && !f.endsWith(".test.ts"))
    .sort();
}

// Only verdict left. See the header: "tracked risk" is no longer an option.
type Verdict = "safe";

type ExitSite = {
  // The file this site lives in.
  readonly file: string;
  // A short, stable substring of source that appears in the lines immediately BEFORE (or on) the
  // call site — content-addressed, so it survives the file being re-flowed/re-numbered. Must be
  // unique enough to identify exactly one call site's context window.
  readonly anchor: string;
  readonly verdict: Verdict;
  readonly reason: string;
};

const KNOWN_EXIT_SITES: readonly ExitSite[] = [
  {
    file: "wire.ts",
    anchor: "console.log : console.error)(text)",
    verdict: "safe",
    reason:
      "usage(exitCode): prints help/usage text and exits. Every call site (arg-parsing errors, " +
      "--help) fires during CLI argv parsing, strictly before any network call in that " +
      "subcommand's flow — nothing pending to race against.",
  },
  {
    file: "wire.ts",
    anchor: "model set: REFUSED",
    verdict: "safe",
    reason:
      "runModelSet (`model set` subcommand): fully synchronous (`function runModelSet(argv): " +
      "void`) and local-file-only (loadRoles/setRoleModel) — no fetch anywhere in this function, " +
      "so no pending socket can exist to race against.",
  },
  {
    file: "wire.ts",
    anchor: "failed to persist ${envVar} to user-scope env",
    verdict: "safe",
    reason:
      "persistKeyForAgents (full `wire`, step 4, Windows path): the preceding operation is a " +
      "SYNCHRONOUS execFileSync (blocks until the child powershell process fully exits and is " +
      "reaped) — not an async fetch — so there is no pending libuv async-handle here; this shape " +
      "is exempt from the fetch-teardown race entirely.",
  },
  {
    file: "wire.ts",
    anchor: "directory does not exist: ${dir}",
    verdict: "safe",
    reason:
      "main(): the directory-existence check is the very first thing main() does after arg " +
      "parsing (before step 1, envVar derivation) — no network call has happened yet anywhere " +
      "in this process, nothing pending.",
  },
  {
    file: "wire.ts",
    anchor: "Minting keys is out of scope for wire.ts",
    verdict: "safe",
    reason:
      "main(), step 2 (key resolution failure): fires before step 3 (validateKey — the FIRST " +
      "network call in the whole `wire` flow). envVar/key resolution (steps 1-2) are local-only " +
      "(process.env / keys.json) — no network call has happened yet.",
  },
  {
    file: "subagent-model-gate.ts",
    anchor: "main()\n    .then(() => process.exit(0))",
    verdict: "safe",
    reason:
      "PreToolUse hook entrypoint, BOTH arms (.then and .catch). No `fetch` exists anywhere in " +
      "this file — it reads stdin, decides locally against harness-models.ts, writes stdout — so " +
      "there is no socket teardown to race. The opposite risk governs instead: this runs on the " +
      "hot path of EVERY tool call, and a process that lingers wedges the session (see the file " +
      "header's ~2h wedge incident), so a guaranteed-immediate exit is the safer end. Output " +
      "truncation is already excluded: writeStdout awaits the write callback before main() " +
      "resolves. Decided explicitly during the package-wide sweep, not overlooked.",
  },
];

// Tight on purpose: two DIFFERENT call sites can sit close enough together that a wider window
// matches both anchors at once. 3 lines is enough to reach each site's own immediately-preceding
// console.error/comment without bleeding into its neighbor's.
const CONTEXT_LOOKBACK_LINES = 3;

type FoundCall = {
  readonly file: string;
  readonly lineIndex: number;
  readonly line: string;
};

// Blank out comments and string bodies so a `process.exit(` inside PROSE is not mistaken for a
// call. Character-preserving (each stripped char becomes a space) so line/column offsets survive.
//
// Known limitation, deliberately not handled: a regex literal containing `//` would be misread as
// a line comment. There is none in this package, and `assertNoUncountedMentions` below is the
// backstop — it cross-checks this scanner against a dumb textual count, so if the stripper ever
// starts dropping a real call the two disagree and this test fails loudly rather than silently
// going blind.
function stripCommentsAndStrings(source: string): string {
  const out = source.split("");
  let i = 0;
  let state: "code" | "line" | "block" | "'" | '"' | "`" = "code";
  while (i < source.length) {
    const c = source[i]!;
    const next = source[i + 1];
    if (state === "code") {
      if (c === "/" && next === "/") {
        state = "line";
        out[i] = " ";
        out[i + 1] = " ";
        i += 2;
        continue;
      }
      if (c === "/" && next === "*") {
        state = "block";
        out[i] = " ";
        out[i + 1] = " ";
        i += 2;
        continue;
      }
      if (c === "'" || c === '"' || c === "`") state = c;
      i++;
      continue;
    }
    if (state === "line") {
      if (c === "\n") state = "code";
      else out[i] = " ";
      i++;
      continue;
    }
    if (state === "block") {
      if (c === "*" && next === "/") {
        out[i] = " ";
        out[i + 1] = " ";
        state = "code";
        i += 2;
        continue;
      }
      if (c !== "\n") out[i] = " ";
      i++;
      continue;
    }
    // inside a string literal
    if (c === "\\") {
      out[i] = " ";
      if (i + 1 < source.length && source[i + 1] !== "\n") out[i + 1] = " ";
      i += 2;
      continue;
    }
    if (c === state) {
      state = "code";
      i++;
      continue;
    }
    if (c !== "\n") out[i] = " ";
    i++;
  }
  return out.join("");
}

// A real call always carries an argument (`process.exit(1)`, `process.exit(exitCode)`, …). The
// argument-less spelling `process.exit()` is this package's documented convention for MENTIONING
// the pattern in prose, and is never a real call here.
const CALL_RE = /process\.exit\(\s*[^)\s]/g;

function findExitCalls(file: string, source: string): readonly FoundCall[] {
  const codeOnly = stripCommentsAndStrings(source);
  const lines = source.split(/\r?\n/);
  const lineStarts: number[] = [];
  {
    let offset = 0;
    for (const line of lines) {
      lineStarts.push(offset);
      offset += line.length + 1;
    }
  }
  const lineOf = (offset: number): number => {
    let lo = 0;
    let hi = lineStarts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (lineStarts[mid]! <= offset) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  };

  const calls: FoundCall[] = [];
  CALL_RE.lastIndex = 0;
  for (let m = CALL_RE.exec(codeOnly); m !== null; m = CALL_RE.exec(codeOnly)) {
    const lineIndex = lineOf(m.index);
    calls.push({ file, lineIndex, line: lines[lineIndex]! });
  }
  return calls;
}

// Backstop for stripCommentsAndStrings (see its comment): count argument-carrying occurrences
// textually, ignoring comment state entirely. Every one of them must be a call this guard found.
// A mismatch means either the stripper went blind to a real call, or someone wrote an
// argument-carrying `process.exit(1)` inside a comment — both must be resolved, not tolerated.
function assertNoUncountedMentions(file: string, source: string, found: readonly FoundCall[]): void {
  const raw: number[] = [];
  const lines = source.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const re = new RegExp(CALL_RE.source, "g");
    if (re.test(lines[i]!)) raw.push(i);
  }
  const foundLines = new Set(found.map((c) => c.lineIndex));
  const uncounted = raw.filter((i) => !foundLines.has(i));
  assert.deepEqual(
    uncounted,
    [],
    `${file}: line(s) ${uncounted.map((i) => i + 1).join(", ")} contain an argument-carrying ` +
      `\`process.exit(…)\` that this guard's comment-stripper did NOT classify as a call. Either ` +
      `it is a real call the stripper missed (fix stripCommentsAndStrings — the guard is blind ` +
      `until you do) or it is prose: comments in this package mention the pattern with the ` +
      `argument-less spelling \`process.exit()\` precisely so this stays unambiguous.`,
  );
}

test("process.exit(...) call sites across the whole package are a closed, justified whitelist (structural guard for the libuv exit race)", () => {
  const files = packageSourceFiles();
  assert.ok(files.length > 5, `sanity check: expected to scan the package's sources, found ${files.length}`);

  const calls: FoundCall[] = [];
  for (const file of files) {
    const source = readFileSync(join(SRC_DIR, file), "utf8");
    const found = findExitCalls(file, source);
    assertNoUncountedMentions(file, source, found);
    calls.push(...found);
  }
  assert.ok(calls.length > 0, "sanity check: the scan itself must find at least the known call sites");

  const problems: string[] = [];
  const matchedAnchors = new Set<string>();

  for (const call of calls) {
    const fileLines = readFileSync(join(SRC_DIR, call.file), "utf8").split(/\r?\n/);
    const windowStart = Math.max(0, call.lineIndex - CONTEXT_LOOKBACK_LINES);
    const context = fileLines.slice(windowStart, call.lineIndex + 1).join("\n");
    const matches = KNOWN_EXIT_SITES.filter((site) => site.file === call.file && context.includes(site.anchor));

    if (matches.length === 0) {
      problems.push(
        `${call.file}:${call.lineIndex + 1}: \`${call.line.trim()}\` — UNRECOGNIZED process.exit() ` +
          `call, not in KNOWN_EXIT_SITES. If any live network call (an awaited fetch / server round ` +
          `trip) can precede it in this process, this is another recurrence of the libuv ` +
          `socket-teardown race that already hit doctor, status, apply, the full \`wire\` command ` +
          `and import-sessions — end the run with \`exitWith(code)\` (then \`return\`), or with ` +
          `\`abortRun(code, message)\` when there is no clean return, both from wire-exit.ts. If no ` +
          `network call can precede it, add it to KNOWN_EXIT_SITES in ` +
          `wire-process-exit-whitelist.test.ts with an anchor + a justification naming WHY — do not ` +
          `let a new exit point go unlisted.`,
      );
      continue;
    }
    if (matches.length > 1) {
      problems.push(
        `${call.file}:${call.lineIndex + 1}: \`${call.line.trim()}\` matches MULTIPLE whitelist ` +
          `anchors (${matches.map((m) => JSON.stringify(m.anchor)).join(", ")}) — anchors must each ` +
          `identify exactly one call site's context window; tighten them.`,
      );
      continue;
    }
    matchedAnchors.add(matches[0]!.anchor);
  }

  assert.equal(
    problems.length,
    0,
    `the package has process.exit() call site(s) not cleanly accounted for:\n${problems.join("\n")}`,
  );

  // The other direction: every whitelist entry must still match something real — a removed or
  // rewritten call site must not leave a stale, unverifiable entry sitting in the whitelist
  // (which would silently defeat this guard the next time a NEW call reuses similar wording).
  const staleEntries = KNOWN_EXIT_SITES.filter((site) => !matchedAnchors.has(site.anchor));
  assert.equal(
    staleEntries.length,
    0,
    `KNOWN_EXIT_SITES has entrie(s) that no longer match any process.exit() call — the call site ` +
      `was removed or reworded; delete or update the stale entry(ies): ` +
      `${staleEntries.map((s) => `${s.file} ${JSON.stringify(s.anchor)}`).join(", ")}`,
  );
});

test("every whitelisted exit site is verdict 'safe' — the class is closed, there is no 'tracked risk' bucket left", () => {
  // The previous version of this guard asserted the OPPOSITE (that at least one tracked-risk entry
  // existed), because six known-defective sites were being tracked rather than fixed. They are
  // fixed now, so that assertion would be false, and keeping a risk bucket at all would re-open
  // the door it took four rounds to close: a site that would need one has exitWith/abortRun
  // available instead, so registering it would be choosing to keep the bug.
  for (const site of KNOWN_EXIT_SITES) {
    assert.equal(
      site.verdict,
      "safe",
      `${site.file} ${JSON.stringify(site.anchor)}: every remaining raw process.exit must be ` +
        `justified as safe, not merely tracked.`,
    );
  }
});

test("the six full-wire sites and import-sessions' two are GONE — no raw process.exit remains where a network call precedes it", () => {
  // A message-improver, NOT the authoritative check: the first test above is what actually closes
  // the class (it fails on ANY unlisted call, anywhere). This one adds a named diagnosis when one
  // of THIS round's specific sites comes back, so the failure reads "site X was reverted" instead
  // of "unrecognized call at line N". It matches on the same 3-line context window, so a revert
  // that also deletes the surrounding explanation will be caught by the first test only — which
  // is fine, because the first test always catches it.
  const fixed: readonly { readonly file: string; readonly anchor: string }[] = [
    { file: "wire.ts", anchor: "[3/10] validate: could not reach" },
    { file: "wire.ts", anchor: "[3/10] validate: server rejected the API key (401)" },
    { file: "wire.ts", anchor: "key belongs to project" },
    { file: "wire.ts", anchor: "[telemetry] could not reach" },
    { file: "wire.ts", anchor: "[telemetry] failed to ensure log" },
    { file: "wire.ts", anchor: "console.error(ws.message)" },
    { file: "wire.ts", anchor: "console.error(e?.stack ?? String(e))" },
    { file: "import-sessions.ts", anchor: "nothing that used to be cut off now runs" },
    { file: "import-sessions.ts", anchor: "CLI and is left unchanged" },
  ];

  for (const { file, anchor } of fixed) {
    const source = readFileSync(join(SRC_DIR, file), "utf8");
    const lines = source.split(/\r?\n/);
    const calls = findExitCalls(file, source);
    for (const call of calls) {
      const windowStart = Math.max(0, call.lineIndex - CONTEXT_LOOKBACK_LINES);
      const context = lines.slice(windowStart, call.lineIndex + 1).join("\n");
      assert.ok(
        !context.includes(anchor),
        `${file}:${call.lineIndex + 1}: a raw \`process.exit(…)\` reappeared at the site anchored by ` +
          `${JSON.stringify(anchor)} — this is one of the sites fixed by wire-six-remaining-exit-races ` +
          `because a completed live network round trip precedes it. Use exitWith/abortRun (wire-exit.ts).`,
      );
    }
  }
});
