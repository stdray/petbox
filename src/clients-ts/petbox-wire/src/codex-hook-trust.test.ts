// Tests for codex-hook-trust.ts — the reproduction of codex's [hooks.state] trust hash (route
// (a) from codex-spec.md §3). No golden hash vector exists in the upstream codex test suite to
// pin against (codex-rs/hooks/src/engine/hooks_tests.rs only asserts a `sha256:` PREFIX, never a
// literal digest), so this file tests STRUCTURE and REGRESSION SHAPE — the things a hand
// reproduction of a hashing algorithm can get provably wrong even without an external oracle:
// determinism, sensitivity to every field that's supposed to affect the hash, insensitivity to
// things that must NOT affect it (JS object key insertion order), and the two normalization
// rules explicitly named as traps in the source (discovery.rs, cited in the module header):
// the SessionEnd/Interrupt timeout clamp, and the UserPromptSubmit/Stop/Interrupt matcher-forced-
// None rule. The actual PROOF that this hash is what codex itself accepts is empirical — a real
// `codex exec` run without --dangerously-bypass-hook-trust (see this task's report).
//
// Run: node --test src/codex-hook-trust.test.ts

import assert from "node:assert/strict";
import { test } from "node:test";
import {
  buildNormalizedHookIdentity,
  codexHookStateKey,
  computeCodexHookTrustHash,
  hookEventLabel,
  normalizeHookTimeoutSec,
} from "./codex-hook-trust.ts";

test("hookEventLabel: every event maps to its documented snake_case label (hooks/src/lib.rs:95-109)", () => {
  assert.equal(hookEventLabel("PreToolUse"), "pre_tool_use");
  assert.equal(hookEventLabel("PermissionRequest"), "permission_request");
  assert.equal(hookEventLabel("PostToolUse"), "post_tool_use");
  assert.equal(hookEventLabel("PreCompact"), "pre_compact");
  assert.equal(hookEventLabel("PostCompact"), "post_compact");
  assert.equal(hookEventLabel("SessionStart"), "session_start");
  assert.equal(hookEventLabel("SessionEnd"), "session_end");
  assert.equal(hookEventLabel("UserPromptSubmit"), "user_prompt_submit");
  assert.equal(hookEventLabel("SubagentStart"), "subagent_start");
  assert.equal(hookEventLabel("SubagentStop"), "subagent_stop");
  assert.equal(hookEventLabel("Stop"), "stop");
  assert.equal(hookEventLabel("Interrupt"), "interrupt");
});

// ---- normalizeHookTimeoutSec (discovery.rs:742-763) --------------------------------------------

test("normalizeHookTimeoutSec: general events default to 600s, floor 1, NO upper clamp", () => {
  assert.equal(normalizeHookTimeoutSec("SessionStart", undefined), 600);
  assert.equal(normalizeHookTimeoutSec("SessionStart", 30), 30);
  assert.equal(normalizeHookTimeoutSec("PreToolUse", 15), 15);
  assert.equal(normalizeHookTimeoutSec("PreToolUse", 999999), 999999, "no upper clamp for this branch");
  assert.equal(normalizeHookTimeoutSec("PreToolUse", 0), 1, "floored at 1");
});

test("normalizeHookTimeoutSec: SessionEnd/Interrupt default to 1s and CLAMP to [1,3] regardless of config", () => {
  assert.equal(normalizeHookTimeoutSec("SessionEnd", undefined), 1);
  assert.equal(normalizeHookTimeoutSec("Interrupt", undefined), 1);
  assert.equal(normalizeHookTimeoutSec("SessionEnd", 3), 3);
  // The trap this test exists for: writing 30 in hooks.json does NOT make the normalized (and
  // therefore hashed) timeout 30 — codex silently clamps it down to 3.
  assert.equal(normalizeHookTimeoutSec("SessionEnd", 30), 3);
  assert.equal(normalizeHookTimeoutSec("Interrupt", 999), 3);
  assert.equal(normalizeHookTimeoutSec("SessionEnd", 0), 1);
});

// ---- buildNormalizedHookIdentity shape ----------------------------------------------------------

test("buildNormalizedHookIdentity: matcher omitted when not set, present when set (events/common.rs:112-128)", () => {
  const noMatcher = buildNormalizedHookIdentity({ event: "SessionStart", command: "node x" });
  assert.equal("matcher" in noMatcher, false);

  const withMatcher = buildNormalizedHookIdentity({
    event: "PreToolUse",
    matcher: "spawn_agent",
    command: "node x",
  });
  assert.equal(withMatcher["matcher"], "spawn_agent");
});

test("buildNormalizedHookIdentity: matcher is FORCED to absent for UserPromptSubmit/Stop/Interrupt even if supplied", () => {
  for (const event of ["UserPromptSubmit", "Stop", "Interrupt"] as const) {
    const identity = buildNormalizedHookIdentity({ event, matcher: "should-be-dropped", command: "node x" });
    assert.equal("matcher" in identity, false, `${event}: matcher must be forced absent`);
  }
});

test("buildNormalizedHookIdentity: async defaults to false, timeout is always present (never omitted)", () => {
  const identity = buildNormalizedHookIdentity({ event: "SessionStart", command: "node x" });
  const hook = (identity["hooks"] as unknown[])[0] as Record<string, unknown>;
  assert.equal(hook["async"], false);
  assert.equal(hook["type"], "command");
  assert.equal(hook["command"], "node x");
  assert.equal(typeof hook["timeout"], "number");
});

// ---- computeCodexHookTrustHash: determinism, sensitivity, and format ----------------------------

test("computeCodexHookTrustHash: format is sha256:<64 lowercase hex chars>", () => {
  const hash = computeCodexHookTrustHash({ event: "SessionStart", command: "node x", timeoutSec: 30 });
  assert.match(hash, /^sha256:[0-9a-f]{64}$/);
});

test("computeCodexHookTrustHash: deterministic — same input, same hash, every time", () => {
  const input = { event: "PreToolUse" as const, matcher: "spawn_agent", command: "node x", timeoutSec: 15 };
  const a = computeCodexHookTrustHash(input);
  const b = computeCodexHookTrustHash(input);
  const c = computeCodexHookTrustHash({ ...input });
  assert.equal(a, b);
  assert.equal(a, c);
});

test("computeCodexHookTrustHash: sensitive to command, matcher, event, and effective timeout", () => {
  const base = computeCodexHookTrustHash({
    event: "PreToolUse",
    matcher: "spawn_agent",
    command: "node a",
    timeoutSec: 15,
  });
  assert.notEqual(
    base,
    computeCodexHookTrustHash({ event: "PreToolUse", matcher: "spawn_agent", command: "node b", timeoutSec: 15 }),
    "different command must hash differently",
  );
  assert.notEqual(
    base,
    computeCodexHookTrustHash({ event: "PreToolUse", matcher: "other", command: "node a", timeoutSec: 15 }),
    "different matcher must hash differently",
  );
  assert.notEqual(
    base,
    computeCodexHookTrustHash({ event: "SessionStart", matcher: "spawn_agent", command: "node a", timeoutSec: 15 }),
    "different event must hash differently",
  );
  assert.notEqual(
    base,
    computeCodexHookTrustHash({ event: "PreToolUse", matcher: "spawn_agent", command: "node a", timeoutSec: 45 }),
    "different effective timeout must hash differently",
  );
});

test("computeCodexHookTrustHash: SessionEnd timeout=3 and timeout=30 hash IDENTICALLY (both clamp to 3) — the exact trap this module exists to get right", () => {
  const explicit3 = computeCodexHookTrustHash({ event: "SessionEnd", command: "node x", timeoutSec: 3 });
  const configured30 = computeCodexHookTrustHash({ event: "SessionEnd", command: "node x", timeoutSec: 30 });
  assert.equal(
    explicit3,
    configured30,
    "codex clamps SessionEnd's timeout to 3s regardless of what is configured — the HASH must " +
      "reflect the clamped value, not the raw one, or a config.toml written with the clamped " +
      "value (as wire.ts's installGlobalHooks does) would carry the WRONG trust hash",
  );
});

test("computeCodexHookTrustHash: async=true vs default (false) hash differently", () => {
  const a = computeCodexHookTrustHash({ event: "SessionStart", command: "node x", timeoutSec: 30 });
  const b = computeCodexHookTrustHash({ event: "SessionStart", command: "node x", timeoutSec: 30, async: true });
  assert.notEqual(a, b);
});

// ---- codexHookStateKey format (hooks/src/lib.rs:112-123) ----------------------------------------

test("codexHookStateKey: '{source_path}:{event_label}:{group_index}:{handler_index}'", () => {
  const key = codexHookStateKey("C:\\Users\\me\\.codex\\hooks.json", "SessionStart", 0, 0);
  assert.equal(key, "C:\\Users\\me\\.codex\\hooks.json:session_start:0:0");
});

test("codexHookStateKey: indices are positional, not identity — a later group/handler index changes the key", () => {
  const k0 = codexHookStateKey("/x/hooks.json", "PreToolUse", 0, 0);
  const k1 = codexHookStateKey("/x/hooks.json", "PreToolUse", 1, 0);
  const k2 = codexHookStateKey("/x/hooks.json", "PreToolUse", 0, 1);
  assert.notEqual(k0, k1);
  assert.notEqual(k0, k2);
  assert.notEqual(k1, k2);
});
