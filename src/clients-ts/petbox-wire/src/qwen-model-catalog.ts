// Single source of truth for the qwen `modelProviders` entries wire.ts's installGlobalHooks
// writes into $QWEN_HOME/settings.json — the exact model ids the kit registers for qwen on this
// machine (task wire-support-codex-qwen, model-registration-check follow-up).
//
// WHY THIS EXISTS SEPARATELY FROM wire.ts: these two model lists used to be literals declared
// inline inside installGlobalHooks (a non-exported function deep in a file that runs main() at
// import time — see wire.ts's own header for why nothing testable lives there directly). That
// made them unreachable from anywhere else, so the model-registration-check module (which needs
// to know exactly which qwen ids the kit registers, to warn when a role binding names one it
// doesn't) would have had no choice but to re-list the same four ids by hand — a second source of
// truth for "what ids exist" that WOULD have silently drifted the next time someone edited one
// list and not the other (this file's header exists precisely because that class of bug is what
// this whole task is closing). Lifting the two arrays out here and having wire.ts import them
// makes drift structurally impossible instead of merely unlikely.
//
// Unlike codex (codex-model-catalog.ts), qwen's registered set is NOT derived from roles.json —
// wire.ts writes these exact four ids into modelProviders every run regardless of what is bound
// (`modelProviders` merges as REPLACE — see installGlobalHooks's own comment), so this file's
// arrays ARE the complete registered set, full stop. See roles.ts's QWEN_ROLE_MODEL_SEED and
// wire.ts's installGlobalHooks for the "why these four, why split direct/opencode-go" reasoning.
//
// Plain TS for native node type-stripping: zero deps.

export type QwenModelEntry = {
  readonly id: string;
  readonly name: string;
  readonly wireModel: string;
};

/** Direct-DeepSeek-subscription models, registered under `modelProviders.deepseek`. */
export const QWEN_DEEPSEEK_MODELS: readonly QwenModelEntry[] = [
  { id: "ds-deepseek-v4-pro", name: "DeepSeek V4 Pro (direct)", wireModel: "deepseek-v4-pro" },
  { id: "ds-deepseek-v4-flash", name: "DeepSeek V4 Flash (direct)", wireModel: "deepseek-v4-flash" },
];

/** opencode-go-gateway models, registered under `modelProviders.opencode-go`. Stays registered
 * even though no role binds through it today (roles.ts's QWEN_ROLE_MODEL_SEED comment) — a
 * second subscription the kit documents for a future rebinding, not dead weight. */
export const QWEN_OPENCODE_GO_MODELS: readonly QwenModelEntry[] = [
  { id: "go-glm-5.3-flash", name: "GLM 5.3 Flash (opencode-go)", wireModel: "glm-5.3-flash" },
  { id: "go-qwen3.8-max", name: "Qwen3.8 Max (opencode-go)", wireModel: "qwen3.8-max" },
];

/** Every bare model id the kit registers for qwen on this machine (both provider keys). Bare —
 * never the `authType:id` form a role .md file's `model:` frontmatter uses (qwen-spec.md §5) —
 * matching how qwen itself keys `modelProviders.<key>[].id` and `model.name` (wire.ts's
 * installGlobalHooks comment on qwenDefaultModelName). */
export function qwenRegisteredModelIds(): string[] {
  return [...QWEN_DEEPSEEK_MODELS, ...QWEN_OPENCODE_GO_MODELS].map((m) => m.id);
}
