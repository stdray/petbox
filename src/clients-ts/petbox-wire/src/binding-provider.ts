// Which PROVIDER (subscription / gateway / registry) serves a role→model binding, derived from
// the binding's harness-dialect model value.
//
// WHY THIS EXISTS (task role-model-bindings-review-refactor, defect #4, stage 1): the five
// harnesses write the same model five different ways and only ONE of them puts the provider in
// the value itself. Opening `~/.petbox/roles.json` therefore could not answer "which subscription
// pays for this role" without knowing all five grammars by heart — and that is precisely what the
// Codex/Qwen rollout burned on: those two harnesses ended up on the gateway while opencode's same
// roles sat on the direct subscription, and nothing in the file said so.
//
// WHAT THIS IS NOT (owner decision, handoff `handoff-new-session`): NOT a single canonical model
// grammar translated into each harness's dialect at render time. There is no name valid in all
// five harnesses, and roles.json already IS the correspondence table. The stored `model` value
// stays the harness's own dialect, byte for byte; the provider is a SEPARATE, derived, validatable
// label beside it.
//
// SCOPE (stage boundary): this module only PARSES the stored value by the harness's own grammar.
// Checking the derived provider against LIVE machine config (qwen's `modelProviders`, codex's
// `/models`, `opencode models`, droid's `customModels`) is stages B1/B2 and deliberately absent
// here — nothing in this file reads a config file or the network.
//
// EVERY per-harness rule below is a factual claim, sourced in its own comment (wiki
// `imena-modeley-i-perenosimost-profilya-po-pyati-harnessam` §1 measured 09.09.2026, or the kit's
// own code). Do not invent a rule; an unparseable value derives `null` (honest unknown), never a
// guess.
//
// Plain TS for native node type-stripping: zero deps beyond the qwen id registry leaf.

import { qwenProviderKeyFor } from "./qwen-model-registry.ts";

/**
 * The `model_provider` this kit declares for codex — one per PROCESS, in the config fragment the
 * kit prints for `$CODEX_HOME/config.toml` (owner decision 2026-09-08: codex runs entirely on the
 * direct DeepSeek subscription until a routing proxy exists; since wire-codex-config-print-fragment
 * the kit prints that config rather than writing it, which changes who applies the value, not what
 * it is). codex-config-fragment.ts's CODEX_DEFAULT_PROVIDER aliases this one literal — a second
 * copy is exactly the drift this refactor is closing.
 */
export const CODEX_MODEL_PROVIDER = "deepseek";

/** Provider label for a binding, plus WHY when it could not be determined. */
export type ProviderDerivation = {
  /** The provider/subscription serving this binding, or null when this value cannot name one. */
  readonly provider: string | null;
  /** Human-readable justification — the grammar rule applied, or the reason for null. */
  readonly reason: string;
};

/** A binding that names no concrete model at all (`inherit`, blank) has no provider to name. */
function isUnboundValue(model: string): boolean {
  const m = model.trim();
  return m === "" || m === "inherit";
}

/** Strip a trailing context-window suffix: `opus[1m]` → `opus` (wiki §1: `[1m]` is a context
 * window marker, not part of the id). Mirrors harness-models.ts's own private helper. */
function stripContextSuffix(model: string): string {
  return model.replace(/\[[^\]]*\]$/, "");
}

/**
 * claude-code — provider `anthropic`, or null.
 *
 * Fact: claude-code's `model:` value is a tier alias (`opus|sonnet|haiku|fable`), `inherit`, or a
 * concrete `claude-*` id; there is no provider segment in the grammar at all (wiki §1: "псевдоним,
 * провайдера нет"), and the kit writes no provider configuration for this harness whatsoever
 * (no base-url / gateway key is emitted anywhere in the kit — grep `ANTHROPIC_BASE_URL`: zero
 * hits). Every value the grammar can express therefore belongs to exactly one id namespace,
 * Anthropic's, served by whatever Anthropic account the CLI is authenticated as.
 *
 * The label names the ID NAMESPACE's owner, not a proof of billing route: a user who points the
 * CLI at a compatible gateway through the environment is invisible to roles.json by construction.
 * That is a known limit of the field, stated rather than papered over.
 */
function deriveClaudeCodeProvider(model: string): ProviderDerivation {
  const m = stripContextSuffix(model.trim()).toLowerCase();
  if (m === "haiku" || m === "sonnet" || m === "opus" || m === "fable") {
    return { provider: "anthropic", reason: `'${m}' is a claude-code tier alias — Anthropic's own id namespace` };
  }
  if (/^claude-/i.test(m)) {
    return { provider: "anthropic", reason: `'${m}' is a concrete claude-* id — Anthropic's own id namespace` };
  }
  return {
    provider: null,
    reason:
      `'${model}' is neither a claude-code tier alias nor a claude-* id, so it names no id ` +
      `namespace this harness owns — provider unknown`,
  };
}

/**
 * opencode — the segment before the FIRST `/`.
 *
 * Fact (wiki §1, measured): opencode's value is `<provider>/<model>`; the first `/` is the
 * separator and any further slashes are part of the model id (`lmstudio/openai/gpt-oss-20b`).
 * The provider is a locally configured provider key (`deepseek`, `opencode-go`, `llama.cpp`, …),
 * so this is the one harness where the value already carries the answer.
 *
 * A value with no `/` is not a provider-less opencode id — it is the defect observation
 * `wire-accepts-unprefixed-model-id-opencode` (opencode itself refuses such a value at runtime).
 * Derives null and says so, rather than inventing a default provider.
 */
function deriveOpencodeProvider(model: string): ProviderDerivation {
  const m = model.trim();
  const i = m.indexOf("/");
  if (i <= 0) {
    return {
      provider: null,
      reason:
        `'${model}' has no '<provider>/' prefix — opencode's grammar requires one (observation ` +
        `wire-accepts-unprefixed-model-id-opencode), so no provider can be read off it`,
    };
  }
  return { provider: m.slice(0, i), reason: `opencode grammar: the segment before the first '/'` };
}

/**
 * droid — `custom` (the machine's BYOK registry) or `factory` (Factory's built-in catalog).
 *
 * Fact (wiki §1, measured): droid resolves either a slug from the catalog COMPILED INTO the CLI
 * (30 ids, printed verbatim when it refuses an unknown one) or a `custom:<DisplayName>-<N>` entry
 * from `customModels` in `~/.factory/settings.json`. `custom:` is that BYOK registry's prefix and
 * explicitly NOT a provider slug — so the value cannot name a vendor, and inventing one from the
 * display name (`custom:DeepSeek-V4-Pro-0` → "deepseek") would be a guess: the same display name
 * can front any endpoint the operator configured.
 *
 * What the value CAN say truthfully is which of the two registries resolves it, and that is the
 * question this field exists to answer for droid: `custom` = the operator's own keys and
 * endpoints, `factory` = Factory's bundled catalog on Factory's own subscription. Naming the
 * concrete vendor behind a `custom:` entry needs `~/.factory/settings.json`, which is stage B1.
 */
function deriveDroidProvider(model: string): ProviderDerivation {
  const m = model.trim();
  if (m.startsWith("custom:")) {
    return {
      provider: "custom",
      reason:
        `droid grammar: the 'custom:' prefix is the BYOK registry (customModels in ` +
        `~/.factory/settings.json), not a vendor — the vendor behind it is not expressible here`,
    };
  }
  return { provider: "factory", reason: `droid grammar: a bare slug resolves against Factory's built-in catalog` };
}

/**
 * codex — always the kit's pinned `model_provider`, because codex has exactly one per PROCESS.
 *
 * Fact (wiki §1 + roles.ts's CODEX_ROLE_MODEL_SEED comment, measured): codex's value is a bare
 * provider slug with no provider segment at all; the provider is the separate root scalar
 * `model_provider` in `$CODEX_HOME/config.toml`, one for the whole process, and a per-role
 * `model_provider` field is accepted and silently DROPPED. So the provider is not a property of
 * the binding's value — it is a property of the machine, and the kit pins it (CODEX_MODEL_PROVIDER).
 *
 * This is the harness where the field earns the most: the value alone can NEVER show which
 * subscription serves the role. Whether the pinned provider actually serves this exact slug is a
 * live-config question — stage B2's write gate, not this module's.
 */
function deriveCodexProvider(model: string): ProviderDerivation {
  return {
    provider: CODEX_MODEL_PROVIDER,
    reason:
      `codex pins ONE model_provider per process ('${CODEX_MODEL_PROVIDER}', the value this kit ` +
      `declares in the config fragment it prints) — ` +
      `the binding value '${model.trim()}' is a bare slug that cannot express a provider`,
  };
}

/**
 * qwen — the `modelProviders` key the bare id is registered under, via the kit's own registry.
 *
 * Fact (wiki §1, pinned to chunk-565U2ANU.js:47-75): qwen's value is `<authType>:<id>`, where the
 * pre-colon segment is a CLOSED auth-type enum (`openai|qwen-oauth|gemini|vertex-ai|anthropic`) —
 * an auth TYPE, never a provider — and an unrecognized prefix is silently folded into the id. Both
 * of the kit's provider keys map to protocol `openai`, so the selector collapses them; the provider
 * is recoverable only from the id itself, through the registry that assigned it
 * (qwen-model-registry.ts's `ds-`/`go-` decoration).
 *
 * An id the kit does not register derives null — honest unknown. It is exactly the shape
 * model-registration-check.ts already warns about, and it is what the whole stale-binding class of
 * this refactor looks like from here.
 */
function deriveQwenProvider(model: string): ProviderDerivation {
  const m = model.trim();
  const i = m.indexOf(":");
  const bare = i === -1 ? m : m.slice(i + 1);
  const key = qwenProviderKeyFor(bare);
  if (key === undefined) {
    return {
      provider: null,
      reason:
        `qwen id '${bare}' is not registered by this kit, and qwen's '<authType>:' prefix is an ` +
        `auth type rather than a provider — no provider can be recovered from this value`,
    };
  }
  return { provider: key, reason: `qwen modelProviders key registering the bare id '${bare}'` };
}

/**
 * The provider serving `model` on `harness`, by that harness's own grammar (see each per-harness
 * helper for the sourced rule). `inherit`/blank → null (nothing is bound). An unknown harness →
 * null: making a claim about a harness this kit knows nothing about would be an invention.
 *
 * Pure and offline: never reads a config file, never fetches.
 */
export function deriveBindingProvider(harness: string, model: string): ProviderDerivation {
  if (isUnboundValue(model)) {
    return { provider: null, reason: `'${model.trim() || "(blank)"}' binds no concrete model, so it names no provider` };
  }
  switch (harness) {
    case "claude-code":
      return deriveClaudeCodeProvider(model);
    case "opencode":
      return deriveOpencodeProvider(model);
    case "droid":
      return deriveDroidProvider(model);
    case "codex":
      return deriveCodexProvider(model);
    case "qwen":
      return deriveQwenProvider(model);
    default:
      return { provider: null, reason: `unknown harness '${harness}' — this kit makes no provider claim about it` };
  }
}
