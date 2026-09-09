// codex model_catalog_json builder (codex-spec.md §6) — a FILE Codex reads at startup,
// `{ models: ModelInfo[] }`, that gates two things per slug measured live (task
// wire-support-codex-qwen, local-listener smoke, zero provider tokens spent):
//   - `apply_patch_tool_type: "freeform"` — an uncatalogued slug falls back to
//     `apply_patch_tool_type: None` (models-manager/src/model_info.rs:168-179) and the
//     `apply_patch` tool disappears entirely (9 tools instead of 10, measured).
//   - `context_window` — an uncatalogued slug silently gets 272000 from fallback metadata.
//     Exit 0, no warning either way.
//
// Previously this was THREE HARDCODED SLUGS, entirely disconnected from
// ~/.petbox/roles.json — so `petbox-wire model set worker <other-model> --agent codex` bound
// the role and the catalog gained nothing: the new slug ran uncatalogued, silently losing
// apply_patch and getting the wrong context_window.
//
// Fixed by building the catalog from roles.json's own codex role→model bindings.
// `inherit`/empty bindings are skipped (they name no concrete model). Never emits an empty
// `models` array (codex rejects that outright) — an empty binding set falls back to the kit's
// historical three-slug default, and the caller is told why so this doesn't read as silent
// data loss.
//
// TWO CHANGES, task wire-codex-config-print-fragment (owner decision 09.09.2026, "и назад тоже,
// все унифицировать"):
//
//   1. THE CATALOG IS NO LONGER WRITTEN BY THE KIT — it is PRINTED (codex-config-fragment.ts)
//      for the owner to save themselves, exactly as qwen's `modelProviders` already is. This
//      module is now a pure builder: nothing here touches disk except the roles.json read.
//   2. IT IS BUILT FROM THE ACTIVE PROFILE ONLY (buildCodexModelCatalogFromData), not the union
//      across every profile — refactor defect 6. The union leaked a stale binding living in an
//      UNUSED profile into the config the active profile actually runs on (observed live on
//      `grok-4.6`, observations/codex-reserve-bound-to-unreachable-grok: a model neither
//      configured provider serves, for which a `context_window` had to be invented because
//      nothing can measure it). `collectCodexRoleModelSlugsFromData` (the union) is KEPT and
//      unchanged, because model-registration-check.ts reads it — see its comment there.
//
// `context_window` is no longer the kit's unverified 128000 either: see
// MEASURED_CONTEXT_WINDOWS below.
//
// Lives in its own module (not wire.ts) purely for testability: wire.ts runs main() at import
// time (see its own file header), so nothing meant to be unit-tested can live there directly —
// same reason roles.ts/codex-toml.ts/etc. are their own modules.
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts/qwen-model-catalog.ts.

import { agentLookupKeys, loadRoles, type RolesFile } from "./roles.ts";
import { QWEN_DEEPSEEK_MODELS, QWEN_OPENCODE_GO_MODELS } from "./qwen-model-catalog.ts";

/** The kit's historical three-slug catalog — used ONLY when roles.json has no codex bindings at
 * all (fresh machine, before step 11 seeding runs, or a deliberately emptied roles.json). Also
 * doubles as the known-good display-name map for these three slugs so a still-bound one keeps
 * its exact historical name even after the union-based path takes over. */
const FALLBACK_CODEX_MODELS: readonly { readonly slug: string; readonly displayName: string }[] = [
  { slug: "deepseek-v4-pro", displayName: "DeepSeek V4 Pro" },
  { slug: "deepseek-v4-flash", displayName: "DeepSeek V4 Flash" },
  { slug: "grok-4.6", displayName: "Grok 4.6" },
];

const KNOWN_DISPLAY_NAMES: Readonly<Record<string, string>> = Object.fromEntries(
  FALLBACK_CODEX_MODELS.map((m) => [m.slug, m.displayName]),
);

/** A sensible display name for a slug with no known-good name: title-case each `-`/`_`-separated
 * token, leaving a token that starts with a digit (a version fragment like "4.6"/"v4") alone. */
export function deriveCodexDisplayName(slug: string): string {
  const known = KNOWN_DISPLAY_NAMES[slug];
  if (known) return known;
  return slug
    .split(/[-_]+/)
    .filter((tok) => tok.length > 0)
    .map((tok) => (/^[0-9]/.test(tok) ? tok : tok.charAt(0).toUpperCase() + tok.slice(1)))
    .join(" ");
}

/**
 * Union of every codex role→model binding across every profile in an in-memory RolesFile —
 * sorted, de-duplicated, `inherit`/empty skipped. Stable ordering (sort) so the generated catalog
 * file does not churn from run to run just because Object.entries iterated profiles/roles in a
 * different order.
 *
 * Pure (no disk I/O) so callers already holding a RolesFile in memory — e.g. wire.ts's
 * seedDefaultRoleBindingsIfMissing right after seeding, or the model-registration warning check
 * (model-registration-check.ts) — can reuse the EXACT same union logic that drives
 * model_catalog_json's contents without a redundant re-read of roles.json (and, for the check,
 * without a read-after-write ordering hazard against the seeder's own saveRoles call). This is
 * the single source of truth for "what codex model ids does the kit currently register" —
 * collectCodexRoleModelSlugs (disk-reading) is a thin wrapper around it.
 *
 * KEPT UNION-WIDE ON PURPOSE (task wire-codex-config-print-fragment): the PRINTED catalog moved
 * to the active profile only (collectActiveCodexRoleModelSlugsFromData below, refactor defect 6),
 * but model-registration-check.ts's codex branch compares every binding in every profile against
 * THIS set, which is exactly what makes that branch structurally never fire — see its own header.
 * Narrowing it here would silently switch that check on with a meaning nobody chose (every
 * binding outside the active profile would start warning). Giving codex a check that means
 * something is subtask B1/B2 of the bindings refactor — against the LIVE catalog or the
 * provider's `/models`, never against the kit's own output.
 */
export function collectCodexRoleModelSlugsFromData(data: RolesFile): string[] {
  const slugs = new Set<string>();
  for (const profile of Object.values(data.profiles)) {
    const key = agentLookupKeys("codex").find((k) => k in profile.agents);
    if (!key) continue;
    const roles = profile.agents[key]?.roles ?? {};
    for (const binding of Object.values(roles)) {
      const model = binding.model?.trim();
      if (!model || model === "inherit") continue;
      slugs.add(model);
    }
  }
  return [...slugs].sort();
}

/** Disk-reading wrapper around collectCodexRoleModelSlugsFromData — loads ~/.petbox/roles.json
 * (homeDir injectable for tests) and unions its codex bindings. See that function for the actual
 * logic and why it is split out. */
export function collectCodexRoleModelSlugs(homeDir?: string): string[] {
  return collectCodexRoleModelSlugsFromData(loadRoles(homeDir));
}

/**
 * The codex slugs bound by the ACTIVE profile only — sorted, de-duplicated, `inherit`/empty
 * skipped. This, NOT the all-profiles union above, is what the printed catalog is built from
 * (refactor defect 6): codex reads exactly one config.toml pointing at exactly one catalog, so a
 * binding that lives in a profile nobody has selected has no business shaping it. Measured
 * consequence of the old union behaviour: `grok-4.6`, bound only in the unused `opencode-go-max`
 * / `opencode-direct` profiles, appeared in the active machine's catalog with an invented
 * context_window for a model neither configured provider will serve
 * (observations/codex-reserve-bound-to-unreachable-grok).
 *
 * An unknown/missing active profile yields `[]` — the caller's fallback then applies, exactly as
 * for a roles.json with no codex bindings at all.
 */
export function collectActiveCodexRoleModelSlugsFromData(data: RolesFile): string[] {
  const profile = data.profiles[data.activeProfile];
  if (!profile) return [];
  const key = agentLookupKeys("codex").find((k) => k in profile.agents);
  if (!key) return [];
  const slugs = new Set<string>();
  for (const binding of Object.values(profile.agents[key]?.roles ?? {})) {
    const model = binding.model?.trim();
    if (!model || model === "inherit") continue;
    slugs.add(model);
  }
  return [...slugs].sort();
}

/**
 * Real context windows per WIRE MODEL — MEASURED against the provider endpoint itself, not read
 * off a vendor nameplate: each endpoint was hit with an oversized prompt and the 400 response's
 * own stated limit recorded (wiki `qwen-three-provider-legs-howto`, 09.09.2026). They are facts
 * about a (base_url, wire model) pair, so they are harness-independent — qwen-model-catalog.ts
 * is merely where they were first written down, and this map is DERIVED from it rather than
 * hand-copied, because a second drifting copy of a model table is the exact class of bug this
 * area keeps producing.
 *
 * The mapping is direct: a codex catalog slug IS the wire model name (codex sends `model` to the
 * provider verbatim — `model_providers.deepseek.base_url` is the same api.deepseek.com/v1 the
 * qwen entries measure), whereas qwen needs a decorated `ds-*`/`go-*` id because its own
 * `modelProviders` ids must be globally unique across provider keys.
 *
 * WHY THIS REPLACED 128000: that value was the kit's own invention, carrying the literal comment
 * "kit-chosen default, not verified per-model" — an ~8x understatement against a measured
 * 1048576, and understating a context window is not free: codex compacts on it.
 */
const MEASURED_CONTEXT_WINDOWS: ReadonlyMap<string, number> = new Map(
  [...QWEN_DEEPSEEK_MODELS, ...QWEN_OPENCODE_GO_MODELS].map((m) => [m.wireModel, m.contextWindowSize]),
);

/** What codex itself uses for an UNCATALOGUED slug (fallback metadata, measured — see this
 * file's header). Used as the printed value for a slug the kit has no measurement for, so the
 * catalog entry never makes the window WORSE than not being catalogued at all while still
 * delivering the thing the entry is really for: `apply_patch_tool_type: "freeform"`. Every such
 * slug is reported to the caller (`unmeasuredSlugs`) so the guess is never silent. */
export const CODEX_UNCATALOGUED_CONTEXT_WINDOW = 272000;

/** The context window to publish for a slug, and whether it is a real measurement or codex's own
 * uncatalogued fallback standing in for one. */
export function codexContextWindowFor(slug: string): { readonly contextWindow: number; readonly measured: boolean } {
  const measured = MEASURED_CONTEXT_WINDOWS.get(slug);
  if (measured === undefined) return { contextWindow: CODEX_UNCATALOGUED_CONTEXT_WINDOW, measured: false };
  return { contextWindow: measured, measured: true };
}

// Fields/values follow the VERIFIED-LOADING template in codex-spec.md §6 verbatim
// (shell_type/visibility/truncation_policy/etc.) — only slug, display_name and context_window
// vary per model. context_window is now the MEASURED endpoint limit where one exists
// (MEASURED_CONTEXT_WINDOWS), never the old kit-chosen 128000.
function buildCodexCatalogModel(slug: string, displayName: string): Record<string, unknown> {
  return {
    slug,
    display_name: displayName,
    description: null,
    supported_reasoning_levels: [],
    shell_type: "unified_exec",
    visibility: "list",
    supported_in_api: true,
    priority: 1,
    availability_nux: null,
    upgrade: null,
    support_verbosity: false,
    default_verbosity: null,
    apply_patch_tool_type: "freeform",
    truncation_policy: { mode: "tokens", limit: 65536 },
    // Deliberately left empty — NOT an oversight. An empty list here is itself meaningful (it
    // opts OUT of four tools; a non-empty one would ADD `clock` and
    // `request_user_input_async` — models-manager/src/model_info.rs), and changing the exposed
    // tool set is out of scope for this pass (task wire-support-codex-qwen, Change 2). Only
    // `apply_patch_tool_type` is this catalog's job to gate.
    experimental_supported_tools: [],
    context_window: codexContextWindowFor(slug).contextWindow,
    base_instructions: "You are a coding agent running in the Codex CLI.",
  };
}

export type CodexModelCatalogResult = {
  readonly catalog: { readonly models: Record<string, unknown>[] };
  /** "roles" — derived from the active profile's own codex bindings. "fallback" — that profile
   * binds no codex model at all, so the kit's historical three-slug default was used instead. */
  readonly source: "roles" | "fallback";
  readonly slugs: readonly string[];
  /** Slugs published with codex's own uncatalogued fallback window because the kit has no
   * measurement for them (codexContextWindowFor). Never empty-checked internally — the caller
   * SAYS this out loud rather than shipping a silent guess. */
  readonly unmeasuredSlugs: readonly string[];
};

/** Build codex's model_catalog_json content from the ACTIVE profile's codex role→model bindings
 * (refactor defect 6 — see collectActiveCodexRoleModelSlugsFromData). Falls back to the kit's
 * historical three literals — never an empty `models` array, which codex rejects outright — when
 * the active profile binds no codex model at all. Pure: no disk I/O. */
export function buildCodexModelCatalogFromData(data: RolesFile): CodexModelCatalogResult {
  const bound = collectActiveCodexRoleModelSlugsFromData(data);
  const source = bound.length === 0 ? "fallback" : "roles";
  const entries =
    bound.length === 0
      ? FALLBACK_CODEX_MODELS.map((m) => ({ slug: m.slug, displayName: m.displayName }))
      : bound.map((slug) => ({ slug, displayName: deriveCodexDisplayName(slug) }));
  return {
    catalog: { models: entries.map((e) => buildCodexCatalogModel(e.slug, e.displayName)) },
    source,
    slugs: entries.map((e) => e.slug),
    unmeasuredSlugs: entries.filter((e) => !codexContextWindowFor(e.slug).measured).map((e) => e.slug),
  };
}

/** Disk-reading wrapper around buildCodexModelCatalogFromData — loads ~/.petbox/roles.json
 * (homeDir injectable for tests). */
export function buildCodexModelCatalog(homeDir?: string): CodexModelCatalogResult {
  return buildCodexModelCatalogFromData(loadRoles(homeDir));
}
