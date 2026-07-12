// Per-harness model-id policy for the agent-artifact compiler.
//
// Sibling of harness-capabilities.ts, same contract: EVERY cell is a factual claim about a
// harness taken from that harness's docs or a verified live observation. Do not invent.
//
// WHY THIS EXISTS (intake `wire-apply-writes-unresolvable-model-id`, 2026-07-12):
// `apply` wrote whatever ~/.petbox/roles.json said into the target harness's frontmatter.
// A roles.json whose `claude-code` block had been filled with Factory Droid ids
// (`custom:DeepSeek-V4-Pro-0`) produced `.claude/agents/worker.md` with a model Claude Code
// cannot resolve — and Claude Code does NOT fail on an unresolvable frontmatter model, it
// SILENTLY falls back to the session's model. Workers rode Opus for a whole session without
// anyone noticing. The kit already refused to invent a model; it now also refuses to write
// one the target harness cannot use.
//
// Policy is per harness:
//   closed — the harness's resolvable model ids are enumerable; anything else is a violation.
//   open   — the id space is provider-defined and open-ended (a registry/gateway resolves it);
//            we make no claim, so the gate does not fire. This is honesty, not laxity: a false
//            allow-list would block legitimate ids.
//
// Plain TS for native node type-stripping: zero deps.

import type { HarnessId } from "./harness-capabilities.ts";

export type HarnessModelPolicy =
  | { readonly kind: "closed"; readonly allowed: readonly string[] }
  | { readonly kind: "open"; readonly reason: string };

/**
 * Claude Code frontmatter `model:` — tier aliases + `inherit`, or a concrete Anthropic model id.
 *
 * Aliases: verified live in this repo's roster (.claude/agents/*.md, 2026-07-12) — opus,
 * sonnet, haiku, fable all register and resolve; `inherit` is the documented "use the parent's
 * model" value.
 *
 * Concrete ids: the Anthropic model ids that exist today, per the `claude-api` skill's model
 * catalog (cached 2026-06-24). Deliberately NOT a `claude-*` wildcard: a typo'd or retired id
 * (`claude-sonnet-4.6`, `claude-3-opus-20240229`) is exactly the failure this gate exists to
 * catch. A provider-prefixed id (`anthropic/claude-sonnet-4`, `custom:DeepSeek-V4-Pro-0`) is
 * NOT a Claude Code model id — those belong to opencode / droid.
 *
 * A `[…]` context-window suffix (e.g. `claude-opus-4-8[1m]`) is accepted on a concrete id —
 * that is the form Claude Code itself reports for a 1M-context session.
 */
const CLAUDE_CODE_MODELS: readonly string[] = [
  // tier aliases + inherit (live 2026-07-12)
  "inherit",
  "haiku",
  "sonnet",
  "opus",
  "fable",
  // concrete ids (claude-api skill model catalog)
  "claude-fable-5",
  "claude-opus-4-8",
  "claude-opus-4-7",
  "claude-opus-4-6",
  "claude-opus-4-5",
  "claude-sonnet-5",
  "claude-sonnet-4-6",
  "claude-sonnet-4-5",
  "claude-haiku-4-5",
] as const;

const MODEL_POLICIES: Readonly<Record<HarnessId, HarnessModelPolicy>> = {
  "claude-code": { kind: "closed", allowed: CLAUDE_CODE_MODELS },
  // opencode resolves `provider/model` against whatever providers are configured locally
  // (models.dev catalog + custom providers) — the set is not knowable from the kit.
  opencode: {
    kind: "open",
    reason: "opencode resolves provider/model ids against the locally configured providers",
  },
  // Factory droid frontmatter takes `inherit` or an id from the workspace's model registry,
  // including BYOK `custom:*` entries — not knowable from the kit.
  // https://docs.factory.ai/cli/configuration/custom-droids § Controlling the model
  droid: {
    kind: "open",
    reason: "droid resolves ids against the workspace model registry (incl. custom:* BYOK)",
  },
};

/**
 * Model policy for a harness id.
 * Unknown harness → open (we know nothing about its model space; the capability gate already
 * blocks every role on an unknown harness, so nothing slips through here).
 */
export function harnessModelPolicy(harness: string): HarnessModelPolicy {
  const p = (MODEL_POLICIES as Record<string, HarnessModelPolicy | undefined>)[harness];
  return p ?? { kind: "open", reason: `unknown harness '${harness}' — no model claims` };
}

/** Enumerable resolvable ids for a harness, or null when the harness's id space is open. */
export function allowedModels(harness: string): readonly string[] | null {
  const p = harnessModelPolicy(harness);
  return p.kind === "closed" ? p.allowed : null;
}

/** Strip a trailing context-window suffix: `claude-opus-4-8[1m]` → `claude-opus-4-8`. */
function stripContextSuffix(model: string): string {
  return model.replace(/\[[^\]]*\]$/, "");
}

/**
 * Can `harness` resolve `model`?
 * Open-policy harnesses → always true (no claim). Closed → membership of the allow-list,
 * case-insensitively, ignoring a `[1m]`-style context suffix.
 * An empty/blank model is "not a model" — callers treat unbound separately (warn, not fail).
 */
export function isResolvableModel(harness: string, model: string): boolean {
  const allowed = allowedModels(harness);
  if (allowed === null) return true;
  const m = stripContextSuffix(model.trim()).toLowerCase();
  if (!m) return false;
  return allowed.some((a) => a.toLowerCase() === m);
}
