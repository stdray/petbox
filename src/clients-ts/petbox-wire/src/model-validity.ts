// Is a role→model binding's identifier KNOWN to the live source that would have to resolve it?
//
// WHY THIS EXISTS (task role-model-bindings-review-refactor, defect #3, stage B2): four of the
// five harnesses have an `open` model policy in harness-models.ts, so `model set --agent qwen
// petbox-worker ds-deepsek-v4-pro` (one letter short) was accepted in silence. Qwen itself does
// NOT fail on an unresolvable id either — it quietly falls back to the FIRST registered model of
// the protocol (measured, `getDefaultModelForAuthType`) — so a typo moved a role to a different
// model and nothing anywhere said so. This gate is the only place that mistake can be caught.
//
// ---- THREE OUTCOMES, NEVER TWO -----------------------------------------------------------
//
//   valid       the live source was consulted AND it knows this identifier.
//   invalid     the live source was consulted, it is complete for its own id space, and this
//               identifier is NOT in it.
//   unverified  the source could NOT be consulted at all — no network, no key, no binary on
//               PATH, no config file yet, unparseable answer. This says NOTHING about the model.
//
// Collapsing `unverified` into `invalid` is forbidden, and not on taste grounds: it is exactly
// the defect recorded as `apply-reports-missing-key-as-unregistered-project-and-exits-0` — a
// missing credential reported as a fact about the subject. Measured for codex in stage B1: a
// MISSING key and a WRONG key both answer 401 in ~0.4s, so a 401 cannot distinguish "this model
// does not exist" from "I was not allowed to look". Collapsing them the other way (`invalid`
// silently downgraded to a warning nobody reads) is the original defect #3.
//
// ---- WHAT "valid" DOES NOT MEAN ------------------------------------------------------------
//
// No source below proves the model WORKS. Every one of them proves only that some catalog knows
// the identifier. The live counter-example is `codex`/`reserve` bound to `grok-4.6`: the id is
// real and served by the `opencode-go` provider, while the ACTIVE `model_provider` is `deepseek`,
// which has never heard of it (observation `codex-reserve-bound-to-unreachable-grok`). Reachable
// and existing are different questions; every message this module emits is worded to say which
// one it answered, and none of them promises the other.
//
// ---- BLOCKING IS A PER-HARNESS POLICY, NOT A PROPERTY OF THE VERDICT -----------------------
//
//   claude-code  BLOCKING. The kit owns positive, free, offline knowledge here (the alias list
//                in harness-models.ts) and already refuses the foreign-id shape that caused the
//                2026-07-12 incident. Delegated to classifyModel — no policy is re-derived here.
//                Note the deliberate 2026-07-13 decision this preserves: a concrete `claude-*`
//                id is NOT checked against any enumerable catalog, because a closed catalog of
//                concrete ids produced false blocks on genuinely new models. In this module's
//                vocabulary that tier is `unverified`, which is precisely what it always was.
//   qwen, droid  WARN AND WRITE. Both sources are local files describing THIS machine, and an
//                absent or empty one is a legitimate "not configured yet", not an error — the
//                owner's normal order is roles.json first, provider setup second. Refusing here
//                would punish that order.
//   codex,       WARN AND WRITE, and `unverified` is the common case rather than an accident:
//   opencode     one is a network round trip against a paid provider, the other spawns a foreign
//                CLI. Both fail for reasons that have nothing to do with the model.
//
// ---- COST, AND WHY THERE IS NO DISK CACHE --------------------------------------------------
//
// Measured in stage B1 on the owner's machine: qwen/droid files ~0ms, codex `/models` 0.6-0.7s,
// `opencode models` ~1.1s. A ModelSourceCache memoizes each source for the LIFE OF ONE PROCESS,
// so a batch check of 75 bindings costs one round trip and one spawn, not 75. It is deliberately
// not persisted: a stale on-disk answer would resurrect the exact confusion this module exists to
// prevent — reporting a remembered catalog as a live fact.
//
// Plain TS for native node type-stripping: zero deps beyond node's own stdlib.

import { exec } from "node:child_process";
import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

import { codexHomeDir } from "./codex-paths.ts";
import { getBlockScalar, getRootScalar, parseTomlBlocks } from "./codex-toml.ts";
import { classifyModel } from "./harness-models.ts";
import { qwenHomeDir } from "./qwen-paths.ts";
import { canonicalAgentId, type RolesFile } from "./roles.ts";

export type ModelValidityVerdict = "valid" | "invalid" | "unverified";

export type ModelValidity = {
  readonly verdict: ModelValidityVerdict;
  readonly harness: string;
  readonly model: string;
  /** The live source consulted — or the one that could not be reached. Always named, so a reader
   * can tell WHICH catalog answered (or failed) without reading this file. */
  readonly source: string;
  /** Why this verdict: the rule applied, or the concrete reason the source was unreachable. */
  readonly detail: string;
  /** Does an `invalid` verdict REFUSE the write for this harness, or only warn? Meaningless for
   * the other two verdicts, which never block anywhere. */
  readonly blocking: boolean;
};

/** Default budget for the two expensive sources (codex's HTTP round trip, opencode's spawn).
 * Generous against B1's measurements (0.7s / 1.1s) and small enough that an unreachable source
 * costs a pause, not a hang: a write is never held hostage by a provider being down. */
export const MODEL_SOURCE_TIMEOUT_MS = 5000;

/** Result of consulting one live source: the id set it knows, or why it could not be consulted. */
type SourceSnapshot =
  | { readonly ok: true; readonly ids: readonly string[]; readonly source: string; readonly note: string }
  | { readonly ok: false; readonly source: string; readonly reason: string };

/**
 * Per-process memo of the expensive sources. Create one per batch (checkRolesModelValidity does),
 * or let checkModelValidity create a throwaway for a single check.
 */
export type ModelSourceCache = {
  codex?: Promise<SourceSnapshot>;
  opencode?: Promise<SourceSnapshot>;
};

export function createModelSourceCache(): ModelSourceCache {
  return {};
}

/** Spawn result for `opencode models`, injectable so tests never touch a real CLI. */
export type CommandRun = { readonly ok: true; readonly stdout: string } | { readonly ok: false; readonly reason: string };

export type ModelValidityOptions = {
  readonly homeDir?: string;
  readonly timeoutMs?: number;
  readonly cache?: ModelSourceCache;
  /** Environment the codex provider's `env_key` is looked up in. Injected for tests; production
   * passes nothing and gets `process.env`. */
  readonly env?: Readonly<Record<string, string | undefined>>;
  /** Injected `fetch` for codex's `/models`. Tests pass a stub; nothing else may. */
  readonly fetchImpl?: typeof fetch;
  /** Injected runner for `opencode models`. Tests pass a stub; nothing else may. */
  readonly runOpencodeModels?: (timeoutMs: number) => Promise<CommandRun>;
};

// ---- shared helpers ---------------------------------------------------------------------------

/** A value that binds no concrete model: blank, or the documented "use the parent's" alias. */
function isUnboundValue(model: string): boolean {
  const m = model.trim();
  return m === "" || m === "inherit";
}

/** Strip a trailing context-window suffix: `claude-opus-4-8[1m]` → `claude-opus-4-8`. Mirrors the
 * private helpers in harness-models.ts / binding-provider.ts (same fact, three call sites). */
function stripContextSuffix(model: string): string {
  return model.replace(/\[[^\]]*\]$/, "");
}

/** Unquote a TOML scalar literal (`"https://x"` → `https://x`). Basic strings only — the two
 * fields this module reads (`base_url`, `env_key`) are plain quoted strings in every config the
 * kit has ever printed, and a value it cannot unquote is returned as-is rather than guessed at. */
function unquoteTomlScalar(literal: string): string {
  const t = literal.trim();
  if (t.length >= 2 && ((t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'")))) {
    return t.slice(1, -1);
  }
  return t;
}

function readTextFile(path: string): { readonly ok: true; readonly text: string } | { readonly ok: false; readonly reason: string } {
  try {
    return { ok: true, text: readFileSync(path, "utf8") };
  } catch (e) {
    const code = (e as NodeJS.ErrnoException).code;
    if (code === "ENOENT") return { ok: false, reason: "the file does not exist" };
    return { ok: false, reason: `it could not be read (${code ?? (e instanceof Error ? e.message : String(e))})` };
  }
}

/** Cap a list quoted back to the user — a 43-line catalog in an error message is unreadable. */
function sampleIds(ids: readonly string[], max = 12): string {
  if (ids.length <= max) return ids.join(", ");
  return `${ids.slice(0, max).join(", ")}, … (${ids.length} total)`;
}

// ---- claude-code: the kit's own alias list (offline, blocking) --------------------------------

/**
 * claude-code has no live source and needs none: the id space the kit makes a claim about is the
 * five aliases compiled into harness-models.ts. This function only TRANSLATES that module's
 * three-tier classification into this module's three verdicts — it does not re-derive the policy,
 * and it deliberately keeps the 2026-07-13 decision that a concrete `claude-*` id is unverifiable
 * rather than invalid (a closed catalog of concrete ids produced false blocks on new models).
 */
function checkClaudeCodeModel(model: string): ModelValidity {
  const source = "harness-models.ts CLAUDE_CODE_MODELS (the kit's own alias list — offline)";
  const cls = classifyModel("claude-code", model);
  if (cls === "known") {
    return {
      verdict: "valid",
      harness: "claude-code",
      model,
      source,
      detail: `'${stripContextSuffix(model.trim())}' is one of the aliases claude-code documents`,
      blocking: true,
    };
  }
  if (cls === "unknown") {
    return {
      verdict: "unverified",
      harness: "claude-code",
      model,
      source,
      detail:
        `'${model}' has claude-code's own id shape but the kit keeps NO enumerable catalog of ` +
        `concrete Anthropic ids (decision 2026-07-13: a closed list blocked genuinely new, valid ` +
        `models). Nothing here can confirm or deny it; claude-code itself fails loudly at run time ` +
        `if it is wrong.`,
      blocking: true,
    };
  }
  return {
    verdict: "invalid",
    harness: "claude-code",
    model,
    source,
    detail:
      `'${model}' is neither a claude-code alias nor a claude-* id — it has another harness's id ` +
      `shape (the 2026-07-12 incident shape: a foreign id landing in the wrong harness's binding)`,
    blocking: true,
  };
}

// ---- qwen: $QWEN_HOME/settings.json → modelProviders (local file, warn) -----------------------

/** Every bare id registered in `modelProviders`, with the provider key that registers it. */
function readQwenRegisteredIds(homeDir: string): SourceSnapshot {
  const path = join(qwenHomeDir(homeDir), "settings.json");
  const source = `${path} → modelProviders`;
  const file = readTextFile(path);
  if (!file.ok) {
    return { ok: false, source, reason: `${file.reason} — qwen has no providers configured on this machine yet` };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(file.text);
  } catch (e) {
    return { ok: false, source, reason: `it is not valid JSON (${e instanceof Error ? e.message : String(e)})` };
  }
  const providers = (parsed as { modelProviders?: unknown } | null)?.modelProviders;
  if (providers === undefined || providers === null || typeof providers !== "object") {
    return { ok: false, source, reason: "it declares no `modelProviders` key at all — not configured yet" };
  }
  const ids: string[] = [];
  const labels: string[] = [];
  for (const [key, entries] of Object.entries(providers as Record<string, unknown>)) {
    if (!Array.isArray(entries)) continue;
    for (const entry of entries) {
      const id = (entry as { id?: unknown } | null)?.id;
      if (typeof id === "string" && id.trim()) {
        ids.push(id.trim());
        labels.push(`${id.trim()} (modelProviders.${key})`);
      }
    }
  }
  if (ids.length === 0) {
    return { ok: false, source, reason: "`modelProviders` registers no model ids yet — not configured yet" };
  }
  return { ok: true, ids, source, note: sampleIds(labels) };
}

/**
 * qwen's binding value is `<authType>:<id>` where the pre-colon segment is a CLOSED auth-type
 * enum, never a provider (binding-provider.ts documents the measurement). The id that has to
 * exist in `modelProviders` is the BARE one after the colon.
 */
function checkQwenModel(model: string, homeDir: string): ModelValidity {
  const raw = model.trim();
  const i = raw.indexOf(":");
  const bare = i === -1 ? raw : raw.slice(i + 1);
  const snap = readQwenRegisteredIds(homeDir);
  if (!snap.ok) {
    return {
      verdict: "unverified",
      harness: "qwen",
      model,
      source: snap.source,
      detail:
        `${snap.reason}. Writing the binding anyway: configuring providers after binding roles is ` +
        `the normal order, and an empty source is not evidence against '${bare}'.`,
      blocking: false,
    };
  }
  if (snap.ids.includes(bare)) {
    return {
      verdict: "valid",
      harness: "qwen",
      model,
      source: snap.source,
      detail: `'${bare}' is registered on this machine`,
      blocking: false,
    };
  }
  return {
    verdict: "invalid",
    harness: "qwen",
    model,
    source: snap.source,
    detail:
      `'${bare}' is NOT among the ids this machine registers (${snap.note}). qwen does not fail on ` +
      `an unresolvable id — it silently falls back to the first registered model of the protocol — ` +
      `so this binding would run a DIFFERENT model than it names, with no error anywhere.`,
    blocking: false,
  };
}

// ---- droid: built-in catalog + ~/.factory/settings.json customModels (local, warn) ------------

/**
 * Factory droid's COMPILED-IN catalog, as the CLI itself printed it when refusing an unknown id
 * (`droid exec -m custom:Nope-Model-Probe-0`, droid 0.170.0, measured 2026-09-09 on the owner's
 * machine — stage B1 measured the same shape at 36 ids on an earlier build).
 *
 * A snapshot, and treated as one: a bare slug missing from it produces a NON-BLOCKING `invalid`
 * whose message says a newer droid may have added it. The alternative — spawning `droid exec`
 * with a deliberately bad id on every write, ~2.7s — costs more than the mistake it would catch.
 */
export const DROID_BUILTIN_MODELS: readonly string[] = [
  "auto",
  "claude-opus-4-8",
  "claude-opus-4-8-fast",
  "claude-opus-4-7",
  "claude-opus-4-7-fast",
  "claude-opus-4-6",
  "claude-opus-4-5-20251101",
  "claude-fable-5",
  "claude-sonnet-5",
  "claude-sonnet-4-6",
  "claude-sonnet-4-5-20250929",
  "claude-haiku-4-5-20251001",
  "gpt-5.6-sol",
  "gpt-5.6-terra",
  "gpt-5.6-luna",
  "gpt-5.5",
  "gpt-5.5-fast",
  "gpt-5.5-pro",
  "gpt-5.4",
  "gpt-5.4-fast",
  "gpt-5.4-mini",
  "gpt-5.3-codex",
  "gpt-5.3-codex-fast",
  "gpt-5.2",
  "gemini-3.1-pro-preview",
  "gemini-3.5-flash",
  "gemini-3-flash-preview",
  "glm-5.2",
  "glm-5.2-fast",
  "glm-5.1",
  "kimi-k2.7-code",
  "kimi-k2.6",
  "nemotron-3-ultra",
  "deepseek-v4-pro",
  "minimax-m3",
  "minimax-m2.7",
  "minimax-m2.5",
  "grok-4.5",
] as const;

/** The droid CLI version the catalog above was read off. Quoted in messages so a stale snapshot
 * is self-evident rather than mysterious. */
export const DROID_BUILTIN_CATALOG_VERSION = "0.170.0";

/**
 * `customModels[].id` from `~/.factory/settings.json`.
 *
 * SECURITY: every entry in that array also carries a plaintext `apiKey` — the OWNER's provider
 * credential (measured in stage B1). This function reads the `id` field and nothing else: no
 * other field is copied into a return value, a message, or a log line, and the parsed object
 * goes out of scope with the function. Do not widen it to return whole entries.
 */
function readDroidCustomModelIds(homeDir: string): SourceSnapshot {
  const path = join(homeDir, ".factory", "settings.json");
  const source = `${path} → customModels[].id`;
  const file = readTextFile(path);
  if (!file.ok) {
    return { ok: false, source, reason: `${file.reason} — droid loads custom models from this file, so none are registered` };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(file.text);
  } catch (e) {
    return { ok: false, source, reason: `it is not valid JSON (${e instanceof Error ? e.message : String(e)})` };
  }
  const custom = (parsed as { customModels?: unknown } | null)?.customModels;
  if (!Array.isArray(custom)) {
    return { ok: false, source, reason: "it declares no `customModels` array — no BYOK models are registered yet" };
  }
  const ids: string[] = [];
  for (const entry of custom) {
    const id = (entry as { id?: unknown } | null)?.id;
    if (typeof id === "string" && id.trim()) ids.push(id.trim());
  }
  if (ids.length === 0) {
    return { ok: false, source, reason: "`customModels` is empty — no BYOK models are registered yet" };
  }
  return { ok: true, ids, source, note: sampleIds(ids) };
}

/**
 * droid resolves an id against exactly two registries, and which one applies is decided by the
 * `custom:` prefix (binding-provider.ts derives the same split for the provider label):
 * `custom:*` → the operator's `customModels`; anything else → Factory's compiled-in catalog.
 */
function checkDroidModel(model: string, homeDir: string): ModelValidity {
  const raw = model.trim();
  if (raw.startsWith("custom:")) {
    const snap = readDroidCustomModelIds(homeDir);
    if (!snap.ok) {
      return {
        verdict: "unverified",
        harness: "droid",
        model,
        source: snap.source,
        detail: `${snap.reason}. An unconfigured BYOK registry is not evidence against '${raw}'.`,
        blocking: false,
      };
    }
    if (snap.ids.includes(raw)) {
      return {
        verdict: "valid",
        harness: "droid",
        model,
        source: snap.source,
        detail: `'${raw}' is registered in this machine's BYOK customModels`,
        blocking: false,
      };
    }
    return {
      verdict: "invalid",
      harness: "droid",
      model,
      source: snap.source,
      detail: `'${raw}' is NOT among this machine's customModels ids (${snap.note})`,
      blocking: false,
    };
  }
  const source = `droid's compiled-in catalog (snapshot of CLI ${DROID_BUILTIN_CATALOG_VERSION}, ${DROID_BUILTIN_MODELS.length} ids)`;
  if (DROID_BUILTIN_MODELS.includes(raw)) {
    return {
      verdict: "valid",
      harness: "droid",
      model,
      source,
      detail: `'${raw}' is in Factory's built-in catalog`,
      blocking: false,
    };
  }
  return {
    verdict: "invalid",
    harness: "droid",
    model,
    source,
    detail:
      `'${raw}' is in neither Factory's built-in catalog nor the 'custom:' BYOK namespace. Note the ` +
      `catalog here is a SNAPSHOT of droid ${DROID_BUILTIN_CATALOG_VERSION}: a newer CLI may have ` +
      `added this id. Confirm with \`droid exec -m <bad-id> x\`, which prints its live catalog.`,
    blocking: false,
  };
}

// ---- codex: /models of the ACTIVE model_provider (network, warn, third outcome) ---------------

/** Where codex's active provider lives and how to call it, read from `$CODEX_HOME/config.toml`. */
type CodexProviderTarget =
  | { readonly ok: true; readonly key: string; readonly baseUrl: string; readonly envKey: string | undefined }
  | { readonly ok: false; readonly reason: string };

function readCodexActiveProvider(homeDir: string): CodexProviderTarget {
  const path = join(codexHomeDir(homeDir), "config.toml");
  const file = readTextFile(path);
  if (!file.ok) return { ok: false, reason: `${path}: ${file.reason}` };
  const blocks = parseTomlBlocks(file.text);
  const providerLiteral = getRootScalar(blocks, "model_provider");
  if (providerLiteral === undefined) {
    return { ok: false, reason: `${path} declares no root \`model_provider\`, so no provider is active` };
  }
  const key = unquoteTomlScalar(providerLiteral);
  const header = `model_providers.${key}`;
  const baseUrlLiteral = getBlockScalar(blocks, header, "base_url");
  if (baseUrlLiteral === undefined) {
    return { ok: false, reason: `${path} names provider '${key}' but has no \`[${header}]\` block with a base_url` };
  }
  const envKeyLiteral = getBlockScalar(blocks, header, "env_key");
  return {
    ok: true,
    key,
    baseUrl: unquoteTomlScalar(baseUrlLiteral).replace(/\/+$/, ""),
    envKey: envKeyLiteral === undefined ? undefined : unquoteTomlScalar(envKeyLiteral),
  };
}

/**
 * `GET <base_url>/models` on the ACTIVE provider — the OpenAI-compatible listing both providers
 * in the kit's printed fragment serve (`{object:"list", data:[{id}]}`, measured in stage B1).
 *
 * EVERY failure mode below returns `ok: false`, never an empty id list: an empty list would be
 * indistinguishable from "the provider knows nothing", and a 401 in particular is measured to be
 * identical for a MISSING key and a WRONG one. That is the whole reason `unverified` exists.
 */
async function fetchCodexProviderModels(
  homeDir: string,
  env: Readonly<Record<string, string | undefined>>,
  timeoutMs: number,
  fetchImpl: typeof fetch,
): Promise<SourceSnapshot> {
  const target = readCodexActiveProvider(homeDir);
  if (!target.ok) {
    return { ok: false, source: "codex active provider's /models", reason: target.reason };
  }
  const url = `${target.baseUrl}/models`;
  const source = `${url} (codex model_provider = '${target.key}')`;
  const apiKey = target.envKey === undefined ? undefined : env[target.envKey];
  if (target.envKey !== undefined && (apiKey === undefined || apiKey.trim() === "")) {
    return {
      ok: false,
      source,
      reason:
        `$${target.envKey} is not set in this environment, and /models answers 401 identically for a ` +
        `missing key and a wrong one — so nothing here can be said about the model`,
    };
  }
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    let resp: Response;
    try {
      resp = await fetchImpl(url, {
        method: "GET",
        // Connection: close for the same reason probeWorkspace does it — a kept-alive socket is a
        // libuv handle that outlives this short CLI run and races process teardown on Windows.
        headers: {
          ...(apiKey === undefined ? {} : { Authorization: `Bearer ${apiKey}` }),
          Connection: "close",
        },
        signal: ctrl.signal,
      });
    } catch (e) {
      const aborted = ctrl.signal.aborted;
      return {
        ok: false,
        source,
        reason: aborted
          ? `the request did not answer within ${timeoutMs}ms`
          : `the request failed (${e instanceof Error ? e.message : String(e)})`,
      };
    }
    if (!resp.ok) {
      return {
        ok: false,
        source,
        reason:
          resp.status === 401 || resp.status === 403
            ? `the provider refused the credential (HTTP ${resp.status}) — measured to be the same answer for a missing key and a wrong one, so it is not evidence about the model`
            : `the provider answered HTTP ${resp.status}`,
      };
    }
    let body: unknown;
    try {
      body = await resp.json();
    } catch (e) {
      return { ok: false, source, reason: `the answer was not JSON (${e instanceof Error ? e.message : String(e)})` };
    }
    const data = (body as { data?: unknown } | null)?.data;
    if (!Array.isArray(data)) {
      return { ok: false, source, reason: "the answer carried no `data` array (not an OpenAI-shaped /models listing)" };
    }
    const ids: string[] = [];
    for (const entry of data) {
      const id = (entry as { id?: unknown } | null)?.id;
      if (typeof id === "string" && id.trim()) ids.push(id.trim());
    }
    if (ids.length === 0) {
      return { ok: false, source, reason: "the listing was empty, which names no model either way" };
    }
    return { ok: true, ids, source, note: sampleIds(ids) };
  } finally {
    clearTimeout(timer);
  }
}

function checkCodexModel(model: string, snap: SourceSnapshot): ModelValidity {
  const raw = model.trim();
  if (!snap.ok) {
    return {
      verdict: "unverified",
      harness: "codex",
      model,
      source: snap.source,
      detail: `${snap.reason}. The binding is written; nothing was learned about '${raw}' either way.`,
      blocking: false,
    };
  }
  if (snap.ids.includes(raw)) {
    return {
      verdict: "valid",
      harness: "codex",
      model,
      source: snap.source,
      detail: `'${raw}' is offered by the active provider`,
      blocking: false,
    };
  }
  return {
    verdict: "invalid",
    harness: "codex",
    model,
    source: snap.source,
    detail:
      `'${raw}' is NOT offered by the provider codex is configured to use (${snap.note}). codex pins ` +
      `ONE model_provider per process, so a model another provider serves is unreachable here — that ` +
      `is the \`codex-reserve-bound-to-unreachable-grok\` shape, and it is a statement about REACH, ` +
      `not about the model existing somewhere.`,
    blocking: false,
  };
}

// ---- opencode: `opencode models` stdout (subprocess, warn, third outcome) ---------------------

/**
 * Run `opencode models`. Any failure to run it is a fact about THIS MACHINE, never about the
 * model — hence a reason string rather than an empty catalog, which the caller turns into
 * `unverified`.
 *
 * WHY `exec` AND NOT `execFile`, measured here rather than assumed: on Windows the installed
 * `opencode` is a `.cmd` shim, and since Node's CVE-2024-27980 mitigation `execFile` refuses to
 * spawn one — it throws `spawn EINVAL` SYNCHRONOUSLY out of the call, not through the callback.
 * `execFile(..., { shell: true })` works but prints Node's DEP0190 deprecation warning on every
 * single `model set`. `exec` takes a COMMAND STRING, so it triggers neither: the string here is a
 * compile-time literal with no interpolation, so the argument-escaping hazard DEP0190 warns about
 * does not exist on this path. The try/catch is not decoration — it is the EINVAL class above.
 */
function runOpencodeModelsDefault(timeoutMs: number): Promise<CommandRun> {
  return new Promise<CommandRun>((resolve) => {
    try {
      exec("opencode models", { timeout: timeoutMs, encoding: "utf8", windowsHide: true }, (err, stdout) => {
        if (err) {
          // `exec` reports a missing command through the SHELL, not through errno: cmd.exe says
          // "is not recognized as an internal or external command", sh says "command not found".
          // ExecException.code is the shell's numeric exit status, so the message is the only
          // signal available for this distinction — matched on both spellings, not guessed.
          const notFound = /not recognized|command not found|not found/i.test(err.message);
          resolve({
            ok: false,
            reason: notFound
              ? "`opencode` is not on PATH"
              : `\`opencode models\` failed (exit ${err.code ?? "?"}: ${err.message.trim().split("\n")[0] ?? ""})`,
          });
          return;
        }
        resolve({ ok: true, stdout });
      });
    } catch (e) {
      resolve({ ok: false, reason: `\`opencode models\` could not be started (${e instanceof Error ? e.message : String(e)})` });
    }
  });
}

async function readOpencodeModels(timeoutMs: number, run: (ms: number) => Promise<CommandRun>): Promise<SourceSnapshot> {
  const source = "`opencode models` (the locally configured providers)";
  const res = await run(timeoutMs);
  if (!res.ok) return { ok: false, source, reason: res.reason };
  const ids = res.stdout
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0 && !l.startsWith("#"));
  if (ids.length === 0) {
    return { ok: false, source, reason: "it printed nothing, which names no model either way" };
  }
  return { ok: true, ids, source, note: sampleIds(ids) };
}

function checkOpencodeModel(model: string, snap: SourceSnapshot): ModelValidity {
  const raw = model.trim();
  if (!snap.ok) {
    return {
      verdict: "unverified",
      harness: "opencode",
      model,
      source: snap.source,
      detail: `${snap.reason}. The binding is written; nothing was learned about '${raw}' either way.`,
      blocking: false,
    };
  }
  if (snap.ids.includes(raw)) {
    return {
      verdict: "valid",
      harness: "opencode",
      model,
      source: snap.source,
      detail: `'${raw}' is listed by this machine's opencode`,
      blocking: false,
    };
  }
  const hint = raw.includes("/")
    ? ""
    : ` It also carries no '<provider>/' prefix, which opencode's grammar requires (observation \`wire-accepts-unprefixed-model-id-opencode\`).`;
  return {
    verdict: "invalid",
    harness: "opencode",
    model,
    source: snap.source,
    detail: `'${raw}' is NOT in this machine's \`opencode models\` listing (${snap.note}).${hint}`,
    blocking: false,
  };
}

// ---- entry points -----------------------------------------------------------------------------

/**
 * The verdict on binding `model` to `harness`, against that harness's LIVE source (see the file
 * header for the source table, the three verdicts, and which harness blocks).
 *
 * `inherit`/blank short-circuits to `valid`: it binds no concrete model, so there is nothing to
 * look up and no source to fail. Everything else consults the harness's own source.
 */
export async function checkModelValidity(
  harness: string,
  model: string,
  opts: ModelValidityOptions = {},
): Promise<ModelValidity> {
  const canon = canonicalAgentId(harness);
  if (isUnboundValue(model)) {
    return {
      verdict: "valid",
      harness: canon,
      model,
      source: "(none needed)",
      detail: `'${model.trim() || "(blank)"}' binds no concrete model, so there is no identifier to check`,
      blocking: canon === "claude-code",
    };
  }
  const homeDir = opts.homeDir ?? homedir();
  const timeoutMs = opts.timeoutMs ?? MODEL_SOURCE_TIMEOUT_MS;
  const cache = opts.cache ?? createModelSourceCache();
  switch (canon) {
    case "claude-code":
      return checkClaudeCodeModel(model);
    case "qwen":
      return checkQwenModel(model, homeDir);
    case "droid":
      return checkDroidModel(model, homeDir);
    case "codex": {
      cache.codex ??= fetchCodexProviderModels(homeDir, opts.env ?? process.env, timeoutMs, opts.fetchImpl ?? fetch);
      return checkCodexModel(model, await cache.codex);
    }
    case "opencode": {
      cache.opencode ??= readOpencodeModels(timeoutMs, opts.runOpencodeModels ?? runOpencodeModelsDefault);
      return checkOpencodeModel(model, await cache.opencode);
    }
    default:
      // An unknown harness has no source to consult and the kit makes no claim about its id
      // space — saying "invalid" would be an invention, saying "valid" would be a lie.
      return {
        verdict: "unverified",
        harness: canon,
        model,
        source: "(no source known for this harness)",
        detail: `this kit knows no model source for harness '${canon}', so it can make no claim about '${model.trim()}'`,
        blocking: false,
      };
  }
}

/** One binding's coordinates plus its verdict — the batch checker's row type. */
export type RoleBindingValidity = {
  readonly profile: string;
  readonly agent: string;
  readonly role: string;
  readonly validity: ModelValidity;
};

/**
 * Check EVERY binding in a roles file against the live sources, READ-ONLY: this function never
 * writes roles.json and never touches a harness config. One ModelSourceCache is shared across the
 * whole sweep, so the two expensive sources are consulted once for the entire file rather than
 * once per binding.
 */
export async function checkRolesModelValidity(
  data: RolesFile,
  opts: ModelValidityOptions = {},
): Promise<RoleBindingValidity[]> {
  const cache = opts.cache ?? createModelSourceCache();
  const rows: RoleBindingValidity[] = [];
  for (const [profile, prof] of Object.entries(data.profiles)) {
    for (const [agent, agentRoles] of Object.entries(prof.agents)) {
      for (const [role, binding] of Object.entries(agentRoles.roles)) {
        rows.push({
          profile,
          agent,
          role,
          validity: await checkModelValidity(agent, binding.model, { ...opts, cache }),
        });
      }
    }
  }
  return rows;
}

/** One-line rendering of a verdict, with the verdict word FIRST so a reader never has to infer
 * which of the three outcomes they are looking at. */
export function formatModelValidity(v: ModelValidity): string {
  const label =
    v.verdict === "valid" ? "OK" : v.verdict === "invalid" ? (v.blocking ? "INVALID (refused)" : "INVALID") : "UNVERIFIED";
  return `${label}: ${v.detail}\n  source: ${v.source}`;
}
