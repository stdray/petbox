// Warns when a role binding, in ANY profile, names a model id the kit does not currently
// register for that harness — closing the gap task wire-support-codex-qwen's live run exposed:
// seedMissingRoleBindings (roles.ts) deliberately never rewrites a binding that already exists
// (so it never clobbers an operator's own choice), so when the kit's own seed values change (see
// roles.ts's CODEX_ROLE_MODEL_SEED/QWEN_ROLE_MODEL_SEED "REVISED 2026-09-08" comments — both
// collapsed onto the direct DeepSeek subscription), every profile that was NOT the active one at
// the time stays holding the OLD ids forever, invisibly.
//
// Consequence differs sharply by harness (measured live, task wire-support-codex-qwen):
//   - qwen does NOT error on an unresolvable `modelProviders` id — it silently falls back to the
//     FIRST registered model. A stale binding is not cosmetic: switching to that profile silently
//     reroutes every role through it to the wrong model, with no warning from qwen itself.
//   - codex's model_catalog_json is instead built as the UNION of every codex role→model binding
//     across every profile (codex-model-catalog.ts) — so a stale codex binding still gets a real
//     catalog entry and still works if that profile is selected. Structurally this check can
//     never fire for codex today (see this file's own test for why) — it is kept for the same
//     scope the brief asked for (codex is a harness whose registered id set the kit "actually
//     knows"), and because a future change to how the codex catalog is built (e.g. narrowing it
//     off the live union) would make it start doing real work with zero code change here.
//
// Scope is deliberately narrow: ONLY codex and qwen, the two harnesses where the kit itself
// writes the provider/catalog config (model_catalog_json / modelProviders) and can therefore
// enumerate "what ids exist" without guessing. This is NOT the same claim harness-models.ts makes
// (that file's classifyModel is about id SHAPE validity — "does this look like a foreign
// harness's id" — and deliberately calls both codex and qwen's id space "open" because a USER can
// add providers/models the kit knows nothing about). This check is narrower and more concrete:
// "does the kit's OWN generated config currently carry this exact id", which is knowable for
// exactly these two harnesses and no others (opencode/droid/claude-code are excluded — the kit
// does not own their model namespaces; see harness-models.ts's per-harness policy comments).
//
// Both id-set sources are imported, never re-derived here (the class of bug this task closes was
// exactly a second, drifted copy of a seed/registration list):
//   - codex: collectCodexRoleModelSlugsFromData (codex-model-catalog.ts) — the SAME union logic
//     that builds model_catalog_json, run against the in-memory RolesFile already in hand (never
//     a fresh disk read — avoids any read-after-write ordering hazard against the caller's own
//     saveRoles).
//   - qwen: qwenRegisteredModelIds (qwen-model-catalog.ts) — the ids the kit KNOWS about for
//     qwen's `modelProviders` (task wire-print-config-fragment, 09.09.2026: the kit no longer
//     WRITES modelProviders itself — see qwen-config-fragment.ts — this is now the catalog a
//     printed fragment and this check both read, not a live-config mirror).
//
// Non-blocking by design (brief: "warn, do not auto-fix and do not refuse the run"): rewriting an
// operator's binding is exactly what seedMissingRoleBindings itself refuses to do, and a hard
// failure here would block a legitimate operator who registers extra models some other way this
// kit cannot see.
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts and the two catalog modules.

import { agentLookupKeys, type RolesFile } from "./roles.ts";
import { collectCodexRoleModelSlugsFromData } from "./codex-model-catalog.ts";
import { qwenRegisteredModelIds } from "./qwen-model-catalog.ts";

/** Harnesses whose registered model-id set the kit can enumerate from its own generated config —
 * see this file's header for why this list stops at exactly these two. */
const CHECKED_HARNESSES = ["codex", "qwen"] as const;
type CheckedHarness = (typeof CHECKED_HARNESSES)[number];

/**
 * Strip a qwen role binding's `authType:` prefix (the form a role .md file's `model:`
 * frontmatter uses, qwen-spec.md §5 — always `openai:<id>` for the ids this kit binds, per
 * roles.ts's QWEN_ROLE_MODEL_SEED comment) down to the bare id that qwen's own
 * `modelProviders.<key>[].id` / `model.name` are keyed by (wire.ts's installGlobalHooks comment
 * on qwenDefaultModelName). Codex bindings never carry a colon, so this is only ever applied to
 * qwen bindings.
 */
function bareModelId(model: string): string {
  const i = model.indexOf(":");
  return i === -1 ? model : model.slice(i + 1);
}

function registeredIdsFor(harness: CheckedHarness, data: RolesFile): ReadonlySet<string> {
  if (harness === "codex") return new Set(collectCodexRoleModelSlugsFromData(data));
  return new Set(qwenRegisteredModelIds());
}

function formatWarning(opts: {
  readonly profile: string;
  readonly harness: CheckedHarness;
  readonly role: string;
  readonly model: string;
}): string {
  const { profile, harness, role, model } = opts;
  const remedy = `petbox-wire model set ${role} <id> --agent ${harness} --profile ${profile}`;
  const consequence =
    harness === "qwen"
      ? "qwen does NOT fail on an unresolvable model id — it silently falls back to the FIRST " +
        "registered model, so selecting this profile would quietly reroute this role (and " +
        "likely others bound the same stale way) to the wrong model with no error at all."
      : "this id is not part of the model_catalog_json the kit currently writes for codex on " +
        "this machine, so codex may run it uncatalogued (loses apply_patch, wrong context_window " +
        "— see codex-model-catalog.ts) unless something else in this roles.json already binds " +
        "the same id and pulls it into the catalog union.";
  return (
    `model registration: profile '${profile}' harness '${harness}' role '${role}' is bound to ` +
    `'${model}', which the kit does not currently register for '${harness}'. ${consequence} ` +
    `Remedy: \`${remedy}\` (pick one of the ids the kit currently registers for '${harness}', or ` +
    `re-register '${model}' yourself first).`
  );
}

/**
 * Every role binding, across every profile, whose model id is not in the set the kit currently
 * registers for that binding's harness — as a fully-formed, ready-to-print warning string per
 * mismatch. Empty when every checked binding resolves. Never throws, never mutates `data`.
 *
 * `data` is the RolesFile already in hand (e.g. wire.ts's seedDefaultRoleBindingsIfMissing, right
 * after seeding) — this never reads roles.json itself, so it always reflects exactly what the
 * caller is about to persist/has just persisted, not a possibly-stale disk copy.
 */
export function findUnregisteredRoleBindings(data: RolesFile): string[] {
  const warnings: string[] = [];
  for (const harness of CHECKED_HARNESSES) {
    const registered = registeredIdsFor(harness, data);
    for (const [profileName, profile] of Object.entries(data.profiles)) {
      const key = agentLookupKeys(harness).find((k) => k in profile.agents);
      if (!key) continue;
      const roles = profile.agents[key]?.roles ?? {};
      for (const [role, binding] of Object.entries(roles)) {
        const raw = binding.model?.trim();
        if (!raw || raw === "inherit") continue; // not a concrete id — nothing to check
        const bare = harness === "qwen" ? bareModelId(raw) : raw;
        if (registered.has(bare)) continue;
        warnings.push(formatWarning({ profile: profileName, harness, role, model: raw }));
      }
    }
  }
  return warnings;
}
