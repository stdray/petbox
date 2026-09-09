// Single source of truth for the four qwen model ids the kit KNOWS about for `modelProviders`
// (task wire-support-codex-qwen, model-registration-check follow-up) — id/name/wireModel/
// contextWindowSize per model, direct-DeepSeek and opencode-go split.
//
// HISTORY / WHY THIS EXISTS SEPARATELY FROM wire.ts: originally these two arrays were the exact
// set wire.ts's installGlobalHooks WROTE into $QWEN_HOME/settings.json every run (`modelProviders`
// merges as REPLACE). Lifted out here so model-registration-check.ts (which needs to know which
// ids the kit registers, to warn when a role binding names one it doesn't) had one source of
// truth instead of a second hand-copied list that would silently drift.
//
// REVISED (task wire-print-config-fragment, owner decision 09.09.2026): installGlobalHooks no
// longer WRITES any of this — `$QWEN_HOME/settings.json`'s `modelProviders` is a hand-maintained
// layout (real `contextWindowSize`, `${session_id}`-templated `customHeaders`, an
// `outboundCorrelation` consent flag) that the kit's REPLACE-merge write was silently destroying.
// This module is now the data source for a PRINTED fragment only, plus the roles.json role→model
// union (`collectQwenRoleModelIdsFromData`, mirroring codex-model-catalog.ts's identical pattern)
// — see qwen-config-fragment.ts for the render/compare logic that consumes both.
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts.

import { agentLookupKeys, loadRoles, type RolesFile } from "./roles.ts";

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

/** Strip a qwen role binding's `authType:` prefix (always literal `openai`, an auth TYPE not a
 * provider slug — roles.ts's QWEN_ROLE_MODEL_SEED comment) down to the bare id qwen's own
 * `modelProviders.<key>[].id` is keyed by. Mirrors model-registration-check.ts's identical
 * `bareModelId` helper (kept duplicated on purpose — that file explicitly does not import this
 * module's role-scanning helpers, only the id-set constants, to avoid a two-way dependency). */
function bareQwenModelId(model: string): string {
  const i = model.indexOf(":");
  return i === -1 ? model : model.slice(i + 1);
}

/**
 * Union of every qwen role→model binding across every profile in an in-memory RolesFile — bare
 * ids, sorted, de-duplicated, `inherit`/empty skipped. Mirrors codex-model-catalog.ts's
 * `collectCodexRoleModelSlugsFromData` (same "why a pure, data-in function" reasoning: callers
 * already holding a RolesFile, e.g. the config-fragment renderer, reuse this without a redundant
 * disk read). This is what makes the printed `agents.modelGrades` fragment react to
 * `petbox-wire model set <role> <id> --agent qwen` — the fragment lists exactly the ids currently
 * bound, not a fixed catalog (task wire-print-config-fragment, acceptance #4).
 */
export function collectQwenRoleModelIdsFromData(data: RolesFile): string[] {
  const ids = new Set<string>();
  for (const profile of Object.values(data.profiles)) {
    const key = agentLookupKeys("qwen").find((k) => k in profile.agents);
    if (!key) continue;
    const roles = profile.agents[key]?.roles ?? {};
    for (const binding of Object.values(roles)) {
      const model = binding.model?.trim();
      if (!model || model === "inherit") continue;
      ids.add(bareQwenModelId(model));
    }
  }
  return [...ids].sort();
}

/** Disk-reading wrapper around collectQwenRoleModelIdsFromData — loads ~/.petbox/roles.json
 * (homeDir injectable for tests) and unions its qwen bindings. */
export function collectQwenRoleModelIds(homeDir?: string): string[] {
  return collectQwenRoleModelIdsFromData(loadRoles(homeDir));
}
