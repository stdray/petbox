// Structural guard for the libuv exit race (apply-exit-race-libuv, and before it
// status-exit-race-libuv, and before that doctor's own two fixes at wire.ts:540-541/548-549):
// a hard `process.exit()` immediately after a live network round trip in the SAME process races
// Windows' async-handle teardown for whatever socket is still closing —
//   Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 76
// — and the caller observes exit 127 instead of the code that call site actually named.
//
// This is the THIRD time this exact shape recurred (doctor, status, apply), and after status's own
// fix no structural guard was left behind — which is exactly how it recurred a third time. A
// BEHAVIORAL test cannot close this gap: the race is timing-dependent. apply-exit-race-libuv.test.ts's
// own regression test (spawning `apply` against a fake local server and forcing a clobber refusal)
// passed 3/3 runs even against the UNFIXED code locally — a behavioral test proves the exit-CODE
// contract (1, not 127, not 0), it cannot prove the race itself is gone or stays gone.
//
// So this is a purely STRUCTURAL sentinel instead: every `process.exit(` call site currently in
// wire.ts is enumerated below by a STABLE CONTENT ANCHOR (never a line number — those drift on the
// next edit) with a verdict:
//   - "safe": no live network call precedes it in that call's own flow — there is nothing pending
//     to race against. Verified by reading the enclosing function, not assumed.
//   - "risk-out-of-scope": the SAME shape as the three fixed bugs (a completed live `fetch`
//     immediately followed by a hard exit, no other await in between) but living in the full
//     `wire` command (steps 3/4/7b), which is OUTSIDE apply-exit-race-libuv's scope (that card is
//     `apply`-only; "не трогай другие подкоманды"). Tracked here rather than silently fixed
//     (scope creep) or silently missed (the exact failure mode this guard exists to prevent) —
//     escalate a follow-up card instead of hand-waving it away.
//
// A NEW, unlisted `process.exit(` call makes this fail LOUDLY, naming why: if it sits after a
// network call, it is (at least) the fourth recurrence of the libuv race and should use
// `process.exitCode = …; unrefLingeringHandles(); return;` instead (see doctor's two exit points /
// status.ts / wire.ts's own runApply for the accepted pattern). If it is genuinely safe, add it to
// KNOWN_EXIT_SITES below with its own anchor + justification — do not let a new exit point go
// unlisted again.
//
// Run: node --test src/wire-process-exit-whitelist.test.ts

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";

const WIRE_TS_PATH = join(import.meta.dirname, "wire.ts");
const source = readFileSync(WIRE_TS_PATH, "utf8");
const lines = source.split(/\r?\n/);

type Verdict = "safe" | "risk-out-of-scope";

type ExitSite = {
  // A short, stable substring of source that appears in the lines immediately BEFORE (or on) the
  // call site — content-addressed, so it survives the file being re-flowed/re-numbered. Must be
  // unique enough to identify exactly one call site's context window.
  readonly anchor: string;
  readonly verdict: Verdict;
  readonly reason: string;
};

const KNOWN_EXIT_SITES: readonly ExitSite[] = [
  {
    anchor: "console.log : console.error)(text)",
    verdict: "safe",
    reason:
      "usage(exitCode): prints help/usage text and exits. Every call site (arg-parsing errors, " +
      "--help) fires during CLI argv parsing, strictly before any network call in that " +
      "subcommand's flow — nothing pending to race against.",
  },
  {
    anchor: "model set: REFUSED",
    verdict: "safe",
    reason:
      "runModelSet (`model set` subcommand): fully synchronous (`function runModelSet(argv): " +
      "void`) and local-file-only (loadRoles/setRoleModel) — no fetch anywhere in this function, " +
      "so no pending socket can exist to race against.",
  },
  {
    anchor: "failed to persist ${envVar} to user-scope env",
    verdict: "safe",
    reason:
      "persistKeyForAgents (full `wire`, step 4, Windows path): the preceding operation is a " +
      "SYNCHRONOUS execFileSync (blocks until the child powershell process fully exits and is " +
      "reaped) — not an async fetch — so there is no pending libuv async-handle here; this shape " +
      "is exempt from the fetch-teardown race entirely.",
  },
  {
    anchor: "[3/10] validate: could not reach",
    verdict: "risk-out-of-scope",
    reason:
      "validateKey (full `wire`, step 3): fires right after `await fetch(...)` throws. Same " +
      "shape as the fixed bugs (a completed live network attempt immediately followed by a hard " +
      "exit). Lives in the full `wire` command, not `apply`/`doctor`/`status` — out of " +
      "apply-exit-race-libuv's scope. NOT verified safe; flag for a follow-up card.",
  },
  {
    anchor: "[3/10] validate: server rejected the API key (401)",
    verdict: "risk-out-of-scope",
    reason:
      "validateKey (full `wire`, step 3): fires right after a completed `await fetch(...)` " +
      "response (401). Same shape as the fixed bugs. Out of apply-exit-race-libuv's scope — " +
      "NOT verified safe; flag for a follow-up card.",
  },
  {
    anchor: "key belongs to project",
    verdict: "risk-out-of-scope",
    reason:
      "validateKey (full `wire`, step 3): fires after `await resp.json()` on a completed fetch " +
      "response, project-key mismatch. Same shape as the fixed bugs. Out of " +
      "apply-exit-race-libuv's scope — NOT verified safe; flag for a follow-up card.",
  },
  {
    anchor: "[telemetry] could not reach",
    verdict: "risk-out-of-scope",
    reason:
      "ensureTelemetryLog (full `wire`, step 7b, --telemetry opt-in only): fires right after " +
      "`await fetch(...)` throws. Same shape as the fixed bugs. Out of apply-exit-race-libuv's " +
      "scope — NOT verified safe; flag for a follow-up card (smaller blast radius: opt-in only).",
  },
  {
    anchor: "[telemetry] failed to ensure log",
    verdict: "risk-out-of-scope",
    reason:
      "ensureTelemetryLog (full `wire`, step 7b, --telemetry opt-in only): fires after a " +
      "completed fetch response that was neither ok nor 409. Same shape as the fixed bugs. Out " +
      "of apply-exit-race-libuv's scope — NOT verified safe; flag for a follow-up card.",
  },
  {
    anchor: "directory does not exist: ${dir}",
    verdict: "safe",
    reason:
      "main(): the directory-existence check is the very first thing main() does after arg " +
      "parsing (before step 1, envVar derivation) — no network call has happened yet anywhere " +
      "in this process, nothing pending.",
  },
  {
    anchor: "Minting keys is out of scope for wire.ts",
    verdict: "safe",
    reason:
      "main(), step 2 (key resolution failure): fires before step 3 (validateKey — the FIRST " +
      "network call in the whole `wire` flow). envVar/key resolution (steps 1-2) are local-only " +
      "(process.env / keys.json) — no network call has happened yet.",
  },
  {
    anchor: "console.error(ws.message)",
    verdict: "risk-out-of-scope",
    reason:
      "main(), step 3b (resolveWorkspace): fires after step 3's `await validateKey(...)` has " +
      "already completed a live round trip. Same shape as the fixed bugs (contrary to this " +
      "card's original assumption that this was one of only two safe sites — it is NOT a child- " +
      "process exit-code forwarding case; it is a locally-computed exit code following a live " +
      "fetch). Out of apply-exit-race-libuv's scope — NOT verified safe; flag for a follow-up card.",
  },
  {
    anchor: "console.error(e?.stack ?? String(e))",
    verdict: "risk-out-of-scope",
    reason:
      "top-level `main().catch(...)`: the last-resort handler for ANY uncaught exception from " +
      "main(), which can itself be thrown while a live fetch elsewhere in the flow (validateKey, " +
      "ensureTelemetryLog, selfSmoke, performApply, …) has just completed. Same shape as the fixed " +
      "bugs in the general case. Out of apply-exit-race-libuv's scope — NOT verified safe; flag " +
      "for a follow-up card.",
  },
];

// Tight on purpose: two DIFFERENT call sites (validateKey's network-error and 401 exits, ~6 lines
// apart) sit close enough together that a wider window matched both anchors at once. 3 lines is
// enough to reach each site's own immediately-preceding console.error/comment without bleeding
// into its neighbor's.
const CONTEXT_LOOKBACK_LINES = 3;

function findRealExitCalls(): { readonly lineIndex: number; readonly line: string }[] {
  const calls: { lineIndex: number; line: string }[] = [];
  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i]!.trim();
    // Real calls always carry an argument (`process.exit(1)`, `process.exit(exitCode)`, …) and are
    // the whole statement on their line in this file's style. Comments that merely MENTION the
    // pattern (e.g. "do NOT hard process.exit()") always use the bare, argument-less spelling —
    // excluded by requiring something other than an immediate `)`.
    if (trimmed.startsWith("process.exit(") && !trimmed.startsWith("process.exit()")) {
      calls.push({ lineIndex: i, line: lines[i]! });
    }
  }
  return calls;
}

test("wire.ts process.exit(...) call sites are a closed, justified whitelist (structural guard for the libuv exit race)", () => {
  const calls = findRealExitCalls();
  assert.ok(calls.length > 0, "sanity check: the scan itself must find at least the known call sites");

  const problems: string[] = [];
  const matchedAnchors = new Set<string>();

  for (const call of calls) {
    const windowStart = Math.max(0, call.lineIndex - CONTEXT_LOOKBACK_LINES);
    const context = lines.slice(windowStart, call.lineIndex + 1).join("\n");
    const matches = KNOWN_EXIT_SITES.filter((site) => context.includes(site.anchor));

    if (matches.length === 0) {
      problems.push(
        `line ${call.lineIndex + 1}: \`${call.line.trim()}\` — UNRECOGNIZED process.exit() call in ` +
          `wire.ts, not in KNOWN_EXIT_SITES. If it sits after a live network call (an awaited fetch / ` +
          `server round trip), this is (at least) the FOURTH recurrence of the libuv socket-teardown ` +
          `race that already hit doctor, status, and apply — fix it with ` +
          `\`process.exitCode = …; unrefLingeringHandles(); return;\` instead of a hard exit, the same ` +
          `pattern used everywhere else in this file. If it is genuinely safe (no live network call ` +
          `precedes it), add it to KNOWN_EXIT_SITES in wire-process-exit-whitelist.test.ts with an ` +
          `anchor + justification — do not let a new exit point go unlisted.`,
      );
      continue;
    }
    if (matches.length > 1) {
      problems.push(
        `line ${call.lineIndex + 1}: \`${call.line.trim()}\` matches MULTIPLE whitelist anchors ` +
          `(${matches.map((m) => JSON.stringify(m.anchor)).join(", ")}) — anchors must each identify ` +
          `exactly one call site; tighten them.`,
      );
      continue;
    }
    matchedAnchors.add(matches[0]!.anchor);
  }

  assert.equal(
    problems.length,
    0,
    `wire.ts has process.exit() call site(s) not cleanly accounted for:\n${problems.join("\n")}`,
  );

  // The other direction: every whitelist entry must still match something real — a removed or
  // rewritten call site must not leave a stale, unverifiable entry sitting in the whitelist
  // (which would silently defeat this guard the next time a NEW call reuses similar wording).
  const staleEntries = KNOWN_EXIT_SITES.filter((site) => !matchedAnchors.has(site.anchor));
  assert.equal(
    staleEntries.length,
    0,
    `KNOWN_EXIT_SITES has entrie(s) that no longer match any process.exit() call in wire.ts — the call ` +
      `site was removed or reworded; delete or update the stale entry(ies): ` +
      `${staleEntries.map((s) => JSON.stringify(s.anchor)).join(", ")}`,
  );

  assert.equal(
    calls.length,
    KNOWN_EXIT_SITES.length,
    `expected exactly ${KNOWN_EXIT_SITES.length} process.exit() call site(s) in wire.ts (see ` +
      `KNOWN_EXIT_SITES), found ${calls.length}.`,
  );
});

test("KNOWN_EXIT_SITES documents at least one open risk (out-of-scope sites are tracked, not hidden)", () => {
  // Guards against someone "fixing" the risk sites silently and forgetting to update this file's
  // verdicts, which would make the whitelist claim a false "all safe" without anyone re-auditing it.
  const risky = KNOWN_EXIT_SITES.filter((s) => s.verdict === "risk-out-of-scope");
  assert.ok(
    risky.length > 0,
    "expected at least one 'risk-out-of-scope' entry (the full `wire` command's step 3/7b exits) — " +
      "if these were actually fixed, update their verdict to 'safe' with a reason citing the fix, " +
      "rather than silently deleting the tracking.",
  );
});
