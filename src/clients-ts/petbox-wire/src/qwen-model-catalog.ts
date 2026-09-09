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
import {
  findQwenModelEntry,
  QWEN_DEEPSEEK_MODELS,
  QWEN_OPENCODE_GO_MODELS,
  QWEN_PROVIDER_KEY_DEEPSEEK,
  QWEN_PROVIDER_KEY_OPENCODE_GO,
  type QwenModelEntry,
  qwenProviderKeyFor,
  qwenRegisteredModelIds,
} from "./qwen-model-registry.ts";

// The catalog DATA (the two provider groups, their ids, and the id -> provider-key lookup) moved
// to the dependency-free leaf qwen-model-registry.ts so binding-provider.ts can read it without
// closing an import cycle back through roles.ts — see that file's header. Re-exported here so
// every existing importer of this module keeps working unchanged.
export {
  findQwenModelEntry,
  QWEN_DEEPSEEK_MODELS,
  QWEN_OPENCODE_GO_MODELS,
  QWEN_PROVIDER_KEY_DEEPSEEK,
  QWEN_PROVIDER_KEY_OPENCODE_GO,
  type QwenModelEntry,
  qwenProviderKeyFor,
  qwenRegisteredModelIds,
};

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
 * `collectCodexRoleModelSlugsFromData` (same "why a pure, data-in function" reasoning).
 *
 * KEPT UNION-WIDE ON PURPOSE, same reasoning as that codex counterpart's own doc comment: the
 * PRINTED `agents.modelGrades` fragment moved to the active profile only
 * (collectActiveQwenRoleModelIdsFromData below, task role-model-bindings-review-refactor,
 * remainder E — the same defect #6 shape codex already had fixed). This union stays exported as
 * the general-purpose "every qwen id the kit currently binds anywhere" query; narrowing it here
 * would silently change the meaning of any other caller without anyone choosing that.
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

/**
 * The qwen bare ids bound by the ACTIVE profile only — sorted, de-duplicated, `inherit`/empty
 * skipped. This, NOT collectQwenRoleModelIdsFromData's all-profiles union above, is what the
 * printed `agents.modelGrades` fragment is built from (task role-model-bindings-review-refactor,
 * remainder E: the SAME defect #6 shape codex-model-catalog.ts's
 * collectActiveCodexRoleModelSlugsFromData already fixed for codex was still live here — qwen
 * reads exactly one settings.json, so a binding that lives in a profile nobody has selected has
 * no business shaping the fragment it prints). An unknown/missing active profile yields `[]` —
 * the caller's fallback then applies, exactly as for a roles.json with no qwen bindings at all.
 */
export function collectActiveQwenRoleModelIdsFromData(data: RolesFile): string[] {
  const profile = data.profiles[data.activeProfile];
  if (!profile) return [];
  const key = agentLookupKeys("qwen").find((k) => k in profile.agents);
  if (!key) return [];
  const ids = new Set<string>();
  for (const binding of Object.values(profile.agents[key]?.roles ?? {})) {
    const model = binding.model?.trim();
    if (!model || model === "inherit") continue;
    ids.add(bareQwenModelId(model));
  }
  return [...ids].sort();
}
