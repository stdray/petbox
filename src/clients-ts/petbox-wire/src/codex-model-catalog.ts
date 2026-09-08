// codex model_catalog_json builder (codex-spec.md §6) — a FILE Codex reads at startup,
// `{ models: ModelInfo[] }`, that gates two things per slug measured live (task
// wire-support-codex-qwen, local-listener smoke, zero provider tokens spent):
//   - `apply_patch_tool_type: "freeform"` — an uncatalogued slug falls back to
//     `apply_patch_tool_type: None` (models-manager/src/model_info.rs:168-179) and the
//     `apply_patch` tool disappears entirely (9 tools instead of 10, measured).
//   - `context_window` — an uncatalogued slug silently gets 272000 from fallback metadata
//     instead of the kit's chosen 128000. Exit 0, no warning either way.
//
// Previously this was THREE HARDCODED SLUGS, entirely disconnected from
// ~/.petbox/roles.json — so `petbox-wire model set worker <other-model> --agent codex` bound
// the role and the catalog gained nothing: the new slug ran uncatalogued, silently losing
// apply_patch and getting the wrong context_window.
//
// Fixed by building the catalog from the UNION of every codex role→model binding across EVERY
// profile in roles.json (not just the active one — a profile switch must not suddenly run an
// uncatalogued model either). `inherit`/empty bindings are skipped (they name no concrete
// model). Never emits an empty `models` array (codex rejects that outright) — an empty union
// falls back to the kit's historical three-slug default, and the caller is told why so this
// doesn't read as silent data loss.
//
// Lives in its own module (not wire.ts) purely for testability: wire.ts runs main() at import
// time (see its own file header), so nothing meant to be unit-tested can live there directly —
// same reason roles.ts/codex-toml.ts/etc. are their own modules.
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts.

import { agentLookupKeys, loadRoles, type RolesFile } from "./roles.ts";

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

// Fields/values follow the VERIFIED-LOADING template in codex-spec.md §6 verbatim
// (shell_type/visibility/truncation_policy/etc.) — only slug, display_name and context_window
// vary per model, and context_window is a kit-chosen default (128000 — not verified per-model;
// codex's own runtime does not reject a merely-generous context_window).
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
    context_window: 128000,
    base_instructions: "You are a coding agent running in the Codex CLI.",
  };
}

export type CodexModelCatalogResult = {
  readonly catalog: { readonly models: Record<string, unknown>[] };
  /** "roles" — derived from a non-empty roles.json union. "fallback" — union was empty, the
   * kit's historical three-slug default was used instead. */
  readonly source: "roles" | "fallback";
  readonly slugs: readonly string[];
};

/** Build codex's model_catalog_json content from the union of every codex role→model binding
 * across every profile in ~/.petbox/roles.json (homeDir injectable for tests). Falls back to the
 * kit's historical three literals — never an empty `models` array, which codex rejects
 * outright — when roles.json has no codex bindings at all. */
export function buildCodexModelCatalog(homeDir?: string): CodexModelCatalogResult {
  const slugs = collectCodexRoleModelSlugs(homeDir);
  if (slugs.length === 0) {
    return {
      catalog: { models: FALLBACK_CODEX_MODELS.map((m) => buildCodexCatalogModel(m.slug, m.displayName)) },
      source: "fallback",
      slugs: FALLBACK_CODEX_MODELS.map((m) => m.slug),
    };
  }
  return {
    catalog: { models: slugs.map((slug) => buildCodexCatalogModel(slug, deriveCodexDisplayName(slug))) },
    source: "roles",
    slugs,
  };
}
