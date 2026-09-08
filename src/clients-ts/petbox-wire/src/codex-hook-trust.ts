// Reproduces Codex CLI's `[hooks.state]` trust hash so `petbox-wire`'s global install can write
// PRE-TRUSTED hook entries into $CODEX_HOME/config.toml (codex-spec.md §3, route (a)) — without
// this, every kit hook is skipped SILENTLY in headless codex runs (no error, zero executions).
//
// Source-anchored (clone D:\my\prj\_analysis\repos\codex @ d6489472, codex 0.153.4):
//   - codex-rs/hooks/src/engine/discovery.rs:764-792 — NormalizedHookIdentity + hook_hash: the
//     hashed shape is `{event_name, matcher?, hooks:[<one normalized handler>]}`.
//   - codex-rs/config/src/fingerprint.rs:48-80 — version_for_toml/canonical_json: TOML -> JSON,
//     every object's keys sorted alphabetically (recursively), `serde_json::to_vec` (compact, no
//     whitespace), SHA-256, formatted `sha256:<hex>`.
//   - codex-rs/hooks/src/engine/discovery.rs:504-575 — how ONE Command handler normalizes before
//     hashing: `command` becomes the PLATFORM-RESOLVED value (`commandWindows` wins on Windows,
//     and the normalized `command_windows` field is then always cleared to None — so
//     "commandWindows" never itself appears as a hashed key); `timeout` is always filled in
//     (never omitted) via normalize_command_hook; `async` is the raw config value (default
//     false); `statusMessage`/`additionalContextLimit` are omitted unless actually set (we never
//     set them). TOML/`toml`-crate struct-field `None` values are omitted from the serialized
//     table entirely (the crate's documented behavior — there is no TOML null).
//   - codex-rs/hooks/src/engine/discovery.rs:742-763 — normalize_command_hook: SessionEnd and
//     Interrupt default the timeout to 1s and CLAMP it to [1,3]s regardless of what is
//     configured; every other event defaults to 600s with only a floor of 1 (no upper clamp).
//   - codex-rs/hooks/src/events/common.rs:112-128 — matcher_pattern_for_event: the matcher is
//     forced to None (omitted from the hash) for UserPromptSubmit/Stop/Interrupt; passed through
//     unchanged for every other event, including SessionStart/SessionEnd (which we never set one
//     for) and PreToolUse (which we do: "spawn_agent").
//   - codex-rs/hooks/src/lib.rs:95-123 — hook_event_key_label (snake_case event labels) and
//     hook_key: the persisted state key is
//     `"{source_path}:{event_label}:{group_index}:{handler_index}"`, where source_path is
//     hooks.json's OWN absolute path exactly as codex resolves it
//     (`config_folder.join("hooks.json").display()` — discovery.rs:343 + :134/:174/:229 — native
//     separators, no canonicalization) and the two indices are this handler's position in the
//     JSON arrays as codex would parse them.
//
// No golden hash vector exists in the upstream test suite to pin against (checked
// codex-rs/hooks/src/engine/hooks_tests.rs — it only asserts a `sha256:` prefix, never a literal
// hash), so this module is covered by structural/regression unit tests here and, more
// importantly, by an EMPIRICAL run of real `codex exec` (see codex-hook-trust.test.ts and this
// task's own report) — the actual proof that a hash this module computes is accepted as trusted.
//
// Plain TS for native node type-stripping: zero deps.

import { createHash } from "node:crypto";

export type CodexHookEvent =
  | "PreToolUse"
  | "PermissionRequest"
  | "PostToolUse"
  | "PreCompact"
  | "PostCompact"
  | "SessionStart"
  | "SessionEnd"
  | "UserPromptSubmit"
  | "SubagentStart"
  | "SubagentStop"
  | "Stop"
  | "Interrupt";

const EVENT_LABELS: Readonly<Record<CodexHookEvent, string>> = {
  PreToolUse: "pre_tool_use",
  PermissionRequest: "permission_request",
  PostToolUse: "post_tool_use",
  PreCompact: "pre_compact",
  PostCompact: "post_compact",
  SessionStart: "session_start",
  SessionEnd: "session_end",
  UserPromptSubmit: "user_prompt_submit",
  SubagentStart: "subagent_start",
  SubagentStop: "subagent_stop",
  Stop: "stop",
  Interrupt: "interrupt",
};

export function hookEventLabel(event: CodexHookEvent): string {
  return EVENT_LABELS[event];
}

// events/common.rs:112-128 — matcher forced to None for these three, regardless of config.
const MATCHER_FORCED_NONE = new Set<CodexHookEvent>(["UserPromptSubmit", "Stop", "Interrupt"]);

/** discovery.rs:742-763 normalize_command_hook, reproduced exactly (including the SessionEnd/
 * Interrupt clamp — a configured `timeout` above 3 for those two is silently clamped DOWN by
 * codex itself, so hashing the raw configured value there would produce the WRONG hash). */
export function normalizeHookTimeoutSec(event: CodexHookEvent, timeoutSec: number | undefined): number {
  if (event === "SessionEnd" || event === "Interrupt") {
    const v = timeoutSec ?? 1;
    return Math.min(Math.max(v, 1), 3);
  }
  return Math.max(timeoutSec ?? 600, 1);
}

function sortKeysDeep(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeysDeep);
  if (value !== null && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(value as Record<string, unknown>).sort()) {
      out[k] = sortKeysDeep((value as Record<string, unknown>)[k]);
    }
    return out;
  }
  return value;
}

export type CommandHookForHash = {
  readonly event: CodexHookEvent;
  /** As written for this MatcherGroup (before matcher_pattern_for_event's forcing). */
  readonly matcher?: string;
  /** The command CODEX ACTUALLY RUNS on the target platform — `commandWindows` if you set one
   * and the target is Windows, else `command`. Callers decide which value this is; this
   * function does not re-derive platform resolution. */
  readonly command: string;
  /** As written (before normalize_command_hook fills/clamps a default). */
  readonly timeoutSec?: number;
  /** Default false, same as the TOML/JSON schema default. */
  readonly async?: boolean;
};

/** Build the exact JSON object codex hashes (pre-canonicalization) — exposed for tests that
 * want to assert on shape, not just the final hex digest. */
export function buildNormalizedHookIdentity(input: CommandHookForHash): Record<string, unknown> {
  const matcher = MATCHER_FORCED_NONE.has(input.event) ? undefined : input.matcher;
  const identity: Record<string, unknown> = {
    event_name: hookEventLabel(input.event),
    hooks: [
      {
        type: "command",
        command: input.command,
        async: input.async ?? false,
        timeout: normalizeHookTimeoutSec(input.event, input.timeoutSec),
      },
    ],
  };
  if (matcher !== undefined) identity["matcher"] = matcher;
  return identity;
}

/**
 * The `trusted_hash` value codex's own discovery would compute for this normalized handler
 * (discovery.rs's hook_hash + fingerprint.rs's version_for_toml/canonical_json). `sha256:<hex>`.
 */
export function computeCodexHookTrustHash(input: CommandHookForHash): string {
  const canonical = sortKeysDeep(buildNormalizedHookIdentity(input));
  const json = JSON.stringify(canonical);
  const hex = createHash("sha256").update(json, "utf8").digest("hex");
  return `sha256:${hex}`;
}

/**
 * The `[hooks.state."<key>"]` key format (hooks/src/lib.rs:112-123, hook_key):
 * "{source_path}:{event_label}:{group_index}:{handler_index}". `hooksJsonPath` must be the
 * IDENTICAL absolute path string codex itself resolves for hooks.json in that config layer
 * (native separators, not canonicalized) — see this module's header for the exact join.
 */
export function codexHookStateKey(
  hooksJsonPath: string,
  event: CodexHookEvent,
  groupIndex: number,
  handlerIndex: number,
): string {
  return `${hooksJsonPath}:${hookEventLabel(event)}:${groupIndex}:${handlerIndex}`;
}
