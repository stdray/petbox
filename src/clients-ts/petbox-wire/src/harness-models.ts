// Per-harness model-id policy for the agent-artifact compiler.
//
// Sibling of harness-capabilities.ts, same contract: EVERY cell is a factual claim about a
// harness taken from that harness's docs or a verified live observation. Do not invent.
//
// WHY THIS EXISTS (intake `wire-apply-writes-unresolvable-model-id`, 2026-07-12; policy
// REVISED 2026-07-13 after a live-fire measurement falsified the original premise — see task
// `model-gate-revision-premise-falsified`): `apply` wrote whatever ~/.petbox/roles.json said
// into the target harness's frontmatter. The 2026-07-12 incident was a roles.json whose
// `claude-code` block had been filled with a Factory Droid id (`custom:DeepSeek-V4-Pro-0`).
// The gate was built on the assumption that Claude Code SILENTLY falls back to the session's
// model on an unresolvable `model:`. A live measurement (`claude -p`, four runs incl. a haiku
// control and the literal incident id) disproved that: Claude Code fails LOUD — zero subagent
// tokens, `Agent terminated early due to an API error: There's an issue with the selected model
// (...)` — for every unresolvable id tried, alias-shaped or concrete-id-shaped alike.
//
// A closed allow-list built on the false premise mostly produced FALSE POSITIVES instead: a
// brand-new id Claude Code would happily accept (e.g. a future `claude-opus-5`) got blocked by
// `apply` before the harness ever saw it, purely because this file's list hadn't caught up yet.
//
// Policy is now THREE-tier per harness:
//   known   — an alias/`inherit` value (or, for an open-policy harness, any id — see below).
//             No violation, no warning.
//   unknown — shape-valid for this harness (looks like one of its own ids, e.g. `claude-*`) but
//             not on the small known-alias list — plausibly a real id newer than this file.
//             NON-BLOCKING: write it and warn. If it is actually wrong, the harness itself now
//             fails loud at runtime (verified above) — an acceptable, visible failure mode this
//             gate no longer needs to preempt.
//   foreign — recognizably ANOTHER harness's id shape (droid's `custom:*` BYOK scheme, or a
//             `provider/model` id — opencode's own syntax) landing in this harness's binding.
//             That is the 2026-07-12 incident shape exactly. BLOCKING: refuse to write it.
//
// Per-harness id-space claim:
//   closed — the harness's KNOWN aliases are enumerable (used for the "known" tier); ids outside
//            that list are classified unknown/foreign by SHAPE, not by a maintained exhaustive
//            catalog (see classifyModel below).
//   open   — the id space is provider-defined and open-ended (a registry/gateway resolves it);
//            we make no claim at all, so the gate never fires. This is honesty, not laxity: a
//            false allow-list would block legitimate ids.
//
// What this gate still cannot see, and remains defensive about: a model that IS valid but is
// excluded by an org `availableModels` policy, where Claude Code is documented to silently fall
// back to inherit. That path has not been measured live (it needs an org policy to test
// against) — it is the reason this gate is not simply deleted, only narrowed.
//
// Plain TS for native node type-stripping: zero deps.

import type { HarnessId } from "./harness-capabilities.ts";

export type HarnessModelPolicy =
  | { readonly kind: "closed"; readonly allowed: readonly string[] }
  | { readonly kind: "open"; readonly reason: string };

/** Three-tier classification of a candidate model id for a given harness (see file header). */
export type ModelClassification = "known" | "unknown" | "foreign";

/**
 * Claude Code frontmatter `model:` known-alias list — tier aliases + `inherit`.
 * Verified live in this repo's roster (.claude/agents/*.md, 2026-07-12) — opus, sonnet, haiku,
 * fable all register and resolve; `inherit` is the documented "use the parent's model" value.
 * Also verified live 2026-07-13: the Task-tool `model` parameter is a CLOSED ENUM restricted to
 * exactly these four tier aliases (`sonnet|opus|haiku|fable`) — a concrete id there is rejected
 * by input-schema validation before any API call. A concrete Anthropic model id (`claude-opus-4-8`
 * and similar) is real and resolvable in frontmatter, but deliberately NOT enumerated here: the
 * catalog changes faster than this file, and an unlisted-but-real id must warn, not block (see
 * classifyModel's "unknown" tier) — that is the whole point of the 2026-07-13 revision.
 */
const CLAUDE_CODE_MODELS: readonly string[] = ["inherit", "haiku", "sonnet", "opus", "fable"] as const;

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

/** Known-alias ids for a harness ("known" tier only), or null when the harness's id space is open. */
export function allowedModels(harness: string): readonly string[] | null {
  const p = harnessModelPolicy(harness);
  return p.kind === "closed" ? p.allowed : null;
}

/** Strip a trailing context-window suffix: `claude-opus-4-8[1m]` → `claude-opus-4-8`. */
function stripContextSuffix(model: string): string {
  return model.replace(/\[[^\]]*\]$/, "");
}

/**
 * Shape of ANOTHER harness's model id landing in this binding: droid's BYOK scheme
 * (`custom:DeepSeek-V4-Pro-0`) or a `provider/model` id (opencode's own syntax, e.g.
 * `deepseek/deepseek-v4-pro`, `anthropic/claude-sonnet-4`). Claude Code frontmatter never uses
 * either shape — this is a naming-convention fact, not a claim about any specific catalog.
 */
function looksLikeForeignHarnessId(model: string): boolean {
  return model.includes("/") || model.includes(":");
}

/**
 * Shape of a Claude Code / Anthropic model id: the `claude-` prefix (case-insensitive), on the
 * id with its context-window suffix already stripped. Every concrete Anthropic id observed to
 * date starts this way — matching it is not a claim that any particular suffix resolves, only
 * that it is plausibly this harness's own id and not a typo of something unrelated.
 */
function looksLikeClaudeId(model: string): boolean {
  return /^claude-/i.test(model);
}

/**
 * Three-tier classification of `model` for `harness` (see file header for the policy).
 * Open-policy harness → always "known" (no claim is made, so nothing is ever flagged).
 * Closed-policy harness (claude-code today):
 *   - exact allow-list membership (case-insensitive, `[1m]`-suffix ignored) → "known";
 *   - else a recognizable foreign-harness shape (`provider/model`, `scheme:id`) → "foreign";
 *   - else this harness's own id shape (`claude-*`) → "unknown" (plausible, unverified);
 *   - else (matches no known shape at all, e.g. `gpt-5`) → "foreign".
 * An empty/blank model has no shape to classify — callers must guard blank separately (unbound
 * is legitimate inherit, not a model to classify; see checkRoleModelTruthfulness).
 */
export function classifyModel(harness: string, model: string): ModelClassification {
  const policy = harnessModelPolicy(harness);
  if (policy.kind === "open") return "known";
  const m = stripContextSuffix(model.trim()).toLowerCase();
  if (!m) return "foreign";
  if (policy.allowed.some((a) => a.toLowerCase() === m)) return "known";
  if (looksLikeForeignHarnessId(m)) return "foreign";
  if (looksLikeClaudeId(m)) return "unknown";
  return "foreign";
}

/**
 * Can `harness` be written `model` at all — i.e. is this NOT the blocking "foreign" tier?
 * True for both "known" and "unknown" (shape-valid, non-blocking) classifications; false only
 * for "foreign". Use classifyModel directly when the known/unknown distinction matters (e.g. to
 * decide whether to emit a non-blocking warning).
 */
export function isResolvableModel(harness: string, model: string): boolean {
  return classifyModel(harness, model) !== "foreign";
}
