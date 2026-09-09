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
// SCOPE (stage boundary): this module PARSES the stored value by the harness's own grammar for
// four of the five harnesses — codex, claude-code, opencode, droid — and reads NO config file or
// network for any of them. checkModelValidity in model-validity.ts (stages B1/B2) remains the
// only place that checks a derived provider against codex's `/models` or `opencode models`.
//
// qwen is the deliberate EXCEPTION, fixed same day as this field shipped (defect
// `qwen-binding-provider-null-for-live-registered-id`): the gate in model-validity.ts already
// reads `$QWEN_HOME/settings.json` → `modelProviders` LIVE to answer "is this id known"; deriving
// `provider` from a separate, hardcoded id list (qwen-model-registry.ts) meant a role the gate had
// just verified as "registered on this machine" could still get `provider: null`, because the two
// questions — "is this id known" and "which subscription serves it" — were answered from two
// different sources about the exact same file. deriveQwenProvider below now reads that SAME live
// file (via qwen-live-providers.ts, ~0ms, a sync local read — not the network/spawn cost the stage
// boundary above is about) and treats it as authoritative when available. The hardcoded list
// survives only as the fallback for when the live file cannot be read at all, so an unconfigured
// machine still gets a `provider` for the ids the kit itself seeds — never a null pretending to be
// certain, and never a guess pretending to be live.
//
// EVERY per-harness rule below is a factual claim, sourced in its own comment (wiki
// `imena-modeley-i-perenosimost-profilya-po-pyati-harnessam` §1 measured 09.09.2026, or the kit's
// own code). Do not invent a rule; an unparseable value derives `null` (honest unknown), never a
// guess.
//
// Plain TS for native node type-stripping: zero deps beyond node's own stdlib (qwen's live read)
// plus the qwen id registry leaf (qwen's offline fallback).

import { homedir } from "node:os";

import { qwenProviderKeyFor } from "./qwen-model-registry.ts";
import { readLiveQwenProviders } from "./qwen-live-providers.ts";

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
 * qwen — the `modelProviders` key the bare id is registered under, read LIVE off this machine's
 * `$QWEN_HOME/settings.json` when that file is available, falling back to the kit's own hardcoded
 * registry only when it is not.
 *
 * Fact (wiki §1, pinned to chunk-565U2ANU.js:47-75): qwen's value is `<authType>:<id>`, where the
 * pre-colon segment is a CLOSED auth-type enum (`openai|qwen-oauth|gemini|vertex-ai|anthropic`) —
 * an auth TYPE, never a provider — and an unrecognized prefix is silently folded into the id. Both
 * of the kit's provider keys map to protocol `openai`, so the selector collapses them; the provider
 * is recoverable only from the id itself, through whatever registered it.
 *
 * Live-first, and AUTHORITATIVE once read (defect `qwen-binding-provider-null-for-live-registered-
 * id`): an id an operator registered themselves — e.g. `ds-deepseek-v4-pro-max` — is invisible to
 * the kit's own hardcoded list (qwen-model-registry.ts) by construction, but IS in
 * `modelProviders`, and the validity gate in model-validity.ts already treats that file as ground
 * truth for "is this id known". Deriving `provider` from a second, stale source produced exactly
 * the contradiction this fix closes: a binding the gate calls "verified" labelled `provider: null`.
 * So when the live file is available, it is the ONLY source consulted — an id missing from it
 * derives null even if the hardcoded list would have recognized it, because the live file is
 * telling the truth about this machine right now and a stale catalog does not get a vote.
 *
 * The hardcoded registry is consulted ONLY when the live file cannot be read at all (missing,
 * unparseable, no `modelProviders` key, or empty) — the "not configured yet" case
 * model-validity.ts's gate treats as `unverified` rather than `invalid`. There, and only there, an
 * id outside BOTH sources derives null — honest unknown, never invented.
 */
function deriveQwenProvider(model: string, homeDir: string): ProviderDerivation {
  const m = model.trim();
  const i = m.indexOf(":");
  const bare = i === -1 ? m : m.slice(i + 1);

  const live = readLiveQwenProviders(homeDir);
  if (live.ok) {
    const key = live.idToProviderKey.get(bare);
    if (key !== undefined) {
      return {
        provider: key,
        reason: `${live.path} → modelProviders key registering the bare id '${bare}' (live, this machine)`,
      };
    }
    return {
      provider: null,
      reason:
        `${live.path} is readable and registers ${live.idToProviderKey.size} id(s), but not '${bare}' ` +
        `— the live file is authoritative once available, so the kit's offline fallback registry is ` +
        `not consulted`,
    };
  }

  const key = qwenProviderKeyFor(bare);
  if (key === undefined) {
    return {
      provider: null,
      reason:
        `${live.path} is unavailable (${live.reason}); qwen id '${bare}' is also not in the kit's ` +
        `own offline fallback registry — no provider can be recovered from this value`,
    };
  }
  return {
    provider: key,
    reason: `${live.path} is unavailable (${live.reason}); falling back to the kit's own offline registry, which registers the bare id '${bare}' under '${key}'`,
  };
}

/**
 * The provider serving `model` on `harness`, by that harness's own grammar (see each per-harness
 * helper for the sourced rule). `inherit`/blank → null (nothing is bound). An unknown harness →
 * null: making a claim about a harness this kit knows nothing about would be an invention.
 *
 * Offline for four of the five harnesses. `qwen` is the exception: it reads
 * `$QWEN_HOME/settings.json` (a sync local file, ~0ms) LIVE — see deriveQwenProvider's doc comment
 * for why. `homeDir` is injectable for tests and defaults to this machine's real home directory,
 * matching every production call site (roles.ts), none of which pass it explicitly.
 */
export function deriveBindingProvider(
  harness: string,
  model: string,
  homeDir: string = homedir(),
): ProviderDerivation {
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
      return deriveQwenProvider(model, homeDir);
    default:
      return { provider: null, reason: `unknown harness '${harness}' — this kit makes no provider claim about it` };
  }
}
