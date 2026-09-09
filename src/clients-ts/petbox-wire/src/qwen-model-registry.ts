// The qwen model ids the kit KNOWS about for `modelProviders`, and which PROVIDER KEY each one
// is registered under. Pure data + pure lookups, ZERO imports — this is a leaf on purpose.
//
// WHY IT IS A SEPARATE FILE FROM qwen-model-catalog.ts (task role-model-bindings-review-refactor,
// stage 1): the catalog module also holds the roles.json-scanning collectors, so it imports
// roles.ts. binding-provider.ts needs the id→provider-key map to label a qwen binding, and
// roles.ts needs binding-provider.ts — importing the catalog from there would close the cycle
// roles.ts → binding-provider.ts → qwen-model-catalog.ts → roles.ts. Splitting the pure data out
// breaks it structurally instead of relying on ESM's tolerance for cycles. qwen-model-catalog.ts
// re-exports every name below, so no existing importer changed.
//
// Plain TS for native node type-stripping: zero deps.

export type QwenModelEntry = {
  readonly id: string;
  readonly name: string;
  readonly wireModel: string;
  /** `modelProviders[].generationConfig.contextWindowSize` — MEASURED, not a vendor nameplate
   * number (wiki `qwen-three-provider-legs-howto`, 09.09.2026): each endpoint was hit with an
   * oversized prompt and the 400 response's own stated limit recorded. Provider entries are
   * hermetic (a top-level `model.generationConfig` does NOT fill a missing provider field per
   * qwen's own settings doc), so this must be rendered INSIDE each entry, never once globally. */
  readonly contextWindowSize: number;
};

/**
 * The `modelProviders` KEY each group is registered under in `$QWEN_HOME/settings.json`.
 *
 * This key is NOT addressable from a role binding's `model:` value and never can be: qwen matches
 * the pre-colon segment of the selector against a CLOSED auth-type enum
 * (`openai|qwen-oauth|gemini|vertex-ai|anthropic`) and silently treats any unknown prefix as part
 * of a bare model id (wiki `imena-modeley-i-perenosimost-profilya-po-pyati-harnessam` §1, pinned to
 * chunk-565U2ANU.js:47-75). Both of the kit's provider keys resolve to the SAME protocol `openai`,
 * so the selector collapses them into one namespace — which is exactly why the ids themselves carry
 * the kit's own `ds-`/`go-` decoration, and exactly why a binding needs a separate provider field
 * to say which subscription serves it. That field is derived from THIS map.
 */
export const QWEN_PROVIDER_KEY_DEEPSEEK = "deepseek";
export const QWEN_PROVIDER_KEY_OPENCODE_GO = "opencode-go";

/** Direct-DeepSeek-subscription models, registered under `modelProviders.deepseek`. */
export const QWEN_DEEPSEEK_MODELS: readonly QwenModelEntry[] = [
  { id: "ds-deepseek-v4-pro", name: "DeepSeek V4 Pro (direct)", wireModel: "deepseek-v4-pro", contextWindowSize: 1048576 },
  { id: "ds-deepseek-v4-flash", name: "DeepSeek V4 Flash (direct)", wireModel: "deepseek-v4-flash", contextWindowSize: 1048576 },
];

/** opencode-go-gateway models, registered under `modelProviders.opencode-go`. Stays registered
 * even though no role binds through it today (roles.ts's QWEN_ROLE_MODEL_SEED comment) — a
 * second subscription the kit documents for a future rebinding, not dead weight. */
export const QWEN_OPENCODE_GO_MODELS: readonly QwenModelEntry[] = [
  { id: "go-glm-5.3-flash", name: "GLM 5.3 Flash (opencode-go)", wireModel: "glm-5.3-flash", contextWindowSize: 1048576 },
  { id: "go-qwen3.8-max", name: "Qwen3.8 Max (opencode-go)", wireModel: "qwen3.8-max", contextWindowSize: 983616 },
];

/** Every bare model id the kit registers for qwen on this machine (both provider keys). Bare —
 * never the `authType:id` form a role .md file's `model:` frontmatter uses (qwen-spec.md §5) —
 * matching how qwen itself keys `modelProviders.<key>[].id` and `model.name` (wire.ts's
 * installGlobalHooks comment on qwenDefaultModelName). */
export function qwenRegisteredModelIds(): string[] {
  return [...QWEN_DEEPSEEK_MODELS, ...QWEN_OPENCODE_GO_MODELS].map((m) => m.id);
}

/** Look up a registered entry by its bare id (either provider key), or undefined if the kit does
 * not know this id (e.g. a role was hand-bound to a model outside this catalog). */
export function findQwenModelEntry(id: string): QwenModelEntry | undefined {
  return [...QWEN_DEEPSEEK_MODELS, ...QWEN_OPENCODE_GO_MODELS].find((m) => m.id === id);
}

/**
 * The `modelProviders` key a bare qwen model id is registered under, or undefined when the kit
 * does not register that id at all.
 *
 * FALLBACK ONLY since defect `qwen-binding-provider-null-for-live-registered-id`: this static map
 * cannot see an id an operator registers themselves (e.g. `ds-deepseek-v4-pro-max`), which is
 * exactly the id space `$QWEN_HOME/settings.json` → `modelProviders` exists to describe.
 * binding-provider.ts's deriveQwenProvider reads that LIVE file first (via
 * qwen-live-providers.ts) and only calls this function when the live file cannot be read at all.
 * Kept for that fallback case (an unconfigured machine still gets a provider for the ids the kit
 * itself seeds) and because qwen-config-fragment.ts's PRINTED fragment still needs an offline id
 * list to render. NOT used by model-registration-check.ts any more (task
 * qwen-model-registration-check-live-source, 09.09.2026): that check now reads the LIVE
 * settings.json via qwen-live-providers.ts, the same source this file's own header explains
 * binding-provider.ts prefers, for the identical reason — this static map cannot see an
 * operator-registered id. See the provider-key doc comment above for why the provider cannot be
 * read off the binding value itself.
 */
export function qwenProviderKeyFor(id: string): string | undefined {
  if (QWEN_DEEPSEEK_MODELS.some((m) => m.id === id)) return QWEN_PROVIDER_KEY_DEEPSEEK;
  if (QWEN_OPENCODE_GO_MODELS.some((m) => m.id === id)) return QWEN_PROVIDER_KEY_OPENCODE_GO;
  return undefined;
}
