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
//   - codex: this check compares every binding against collectCodexRoleModelSlugsFromData — the
//     UNION of every codex role→model binding across every profile — i.e. against ITSELF, so the
//     codex branch is structurally incapable of firing (see this file's own test for why). That
//     was already true when the kit still WROTE model_catalog_json from that same union; it is
//     doubly true now that the kit writes no codex catalog at all (task
//     wire-codex-config-print-fragment) — the union is no longer even a claim about this machine.
//     The branch is kept, unchanged and inert, because giving codex a check that MEANS something
//     has to compare against the LIVE catalog or the provider's `/models`, which is subtask
//     B1/B2 of the bindings refactor and not a tweak here. Do not "revive" it by narrowing the
//     union: see collectCodexRoleModelSlugsFromData's own comment.
//
// Scope is deliberately narrow: ONLY codex and qwen, the two harnesses whose provider/catalog id
// space the kit can enumerate from its own tables (it used to WRITE that config for both; as of
// 09.09.2026 it prints it instead, which is exactly why the codex half of this file is inert —
// see above). This is NOT the same claim harness-models.ts makes
// (that file's classifyModel is about id SHAPE validity — "does this look like a foreign
// harness's id" — and deliberately calls both codex and qwen's id space "open" because a USER can
// add providers/models the kit knows nothing about). This check is narrower and more concrete:
// "does the kit's OWN generated config currently carry this exact id", which is knowable for
// exactly these two harnesses and no others (opencode/droid/claude-code are excluded — the kit
// does not own their model namespaces; see harness-models.ts's per-harness policy comments).
//
// Both id-set sources are imported, never re-derived here (the class of bug this task closes was
// exactly a second, drifted copy of a seed/registration list):
//   - codex: collectCodexRoleModelSlugsFromData (codex-model-catalog.ts) — the all-profiles
//     union, run against the in-memory RolesFile already in hand (never a fresh disk read —
//     avoids any read-after-write ordering hazard against the caller's own saveRoles). NOTE the
//     PRINTED catalog is built from the ACTIVE profile instead (buildCodexModelCatalogFromData);
//     these are deliberately different sets — see the codex bullet at the top of this file.
//   - qwen: readLiveQwenProviders (qwen-live-providers.ts), i.e. the LIVE `$QWEN_HOME/
//     settings.json` → `modelProviders` on THIS machine — REVISED task
//     qwen-model-registration-check-live-source (09.09.2026): this used to read the kit's own
//     hardcoded qwenRegisteredModelIds() (qwen-model-catalog.ts), which was two sources of truth
//     about the same fact — the validity gate (model-validity.ts) already treats the live
//     settings.json as authoritative for "is this id registered", and disagreed with THIS check,
//     which still trusted the hardcoded catalog. Concretely: the owner registers extra ids in
//     `modelProviders` directly (`ds-deepseek-v4-pro-max`, `go-glm-5.3-flash-low` — duplicates of
//     an existing model under a different `reasoning_effort`/grade), the gate correctly calls
//     them "registered on this machine", and this check warned on every `apply` regardless,
//     because the hardcoded catalog has never seen them and never can. Reading the same live file
//     model-validity.ts already reads (via the shared qwen-live-providers.ts leaf, not a second
//     copy) makes the two agree by construction. The hardcoded catalog
//     (qwen-model-catalog.ts/qwen-model-registry.ts) stays — it still backs the PRINTED fragment
//     (qwen-config-fragment.ts) and binding-provider.ts's fallback — just no longer THIS check.
//
// THREE OUTCOMES for qwen, never two — mirrors model-validity.ts's own rule (same file, "THREE
// OUTCOMES, NEVER TWO" in its header) and for the same reason: an unreadable/missing/empty
// settings.json is "nothing was learned", not "this id is unregistered". Collapsing the two would
// resurrect exactly the defect model-validity.ts's header describes
// (`apply-reports-missing-key-as-unregistered-project-and-exits-0`) one file over. So a qwen
// binding whose live file could not be consulted gets its own "could not verify" message
// (formatUnverified), never the "not registered" one (formatWarning) — codex has no live source
// here at all (see the bullet above the THREE OUTCOMES note) so it only ever produces the
// "not registered" shape, unchanged.
//
// Non-blocking by design (brief: "warn, do not auto-fix and do not refuse the run"): rewriting an
// operator's binding is exactly what seedMissingRoleBindings itself refuses to do, and a hard
// failure here would block a legitimate operator who registers extra models some other way this
// kit cannot see.
//
// Plain TS for native node type-stripping: zero deps beyond roles.ts, node:os, and the two live
// sources (codex's in-memory union, qwen's live-file reader).

import { homedir } from "node:os";
import { agentLookupKeys, type RolesFile } from "./roles.ts";
import { collectCodexRoleModelSlugsFromData } from "./codex-model-catalog.ts";
import { readLiveQwenProviders } from "./qwen-live-providers.ts";

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

/** Result of consulting qwen's live source once for the whole sweep: either the bare ids it
 * registers, or why it could not be consulted at all (see the THREE OUTCOMES note above). */
type QwenSnapshot = { readonly ok: true; readonly ids: ReadonlySet<string> } | { readonly ok: false; readonly reason: string };

function qwenSnapshotFor(homeDir: string): QwenSnapshot {
  const live = readLiveQwenProviders(homeDir);
  if (!live.ok) return { ok: false, reason: `${live.path}: ${live.reason}` };
  return { ok: true, ids: new Set(live.idToProviderKey.keys()) };
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
 * qwen's THIRD outcome: the live `modelProviders` file could not be consulted at all (missing,
 * unreadable, unparseable, or empty — readLiveQwenProviders collapses all of those uniformly, see
 * its own doc comment). This is deliberately NOT worded as "not registered" — an unconfigured or
 * momentarily-unreadable machine is not evidence against the id, exactly as model-validity.ts's
 * `unverified` verdict is not.
 */
function formatUnverified(opts: {
  readonly profile: string;
  readonly role: string;
  readonly model: string;
  readonly reason: string;
}): string {
  const { profile, role, model, reason } = opts;
  return (
    `model registration: profile '${profile}' harness 'qwen' role '${role}' is bound to ` +
    `'${model}', but registration could not be checked (${reason}). This is NOT a claim that ` +
    `'${model}' is unregistered — nothing was learned about it either way.`
  );
}

export type FindUnregisteredRoleBindingsOptions = {
  /** Home directory to read qwen's live `settings.json` from. Injectable for tests (mirrors
   * model-validity.ts's ModelValidityOptions); production passes nothing and gets the real one. */
  readonly homeDir?: string;
};

/**
 * Every role binding, across every profile, whose model id is not in the set the kit currently
 * registers for that binding's harness — as a fully-formed, ready-to-print warning string per
 * mismatch (or, for qwen, a "could not verify" string when the live file itself was unreadable —
 * see the THREE OUTCOMES note above). Empty when every checked binding resolves. Never throws,
 * never mutates `data`.
 *
 * `data` is the RolesFile already in hand (e.g. wire.ts's seedDefaultRoleBindingsIfMissing, right
 * after seeding) — this never reads roles.json itself, so it always reflects exactly what the
 * caller is about to persist/has just persisted, not a possibly-stale disk copy. qwen's live
 * `modelProviders`, by contrast, IS read fresh here every call (readLiveQwenProviders is a ~0ms
 * local file read, per qwen-live-providers.ts/model-validity.ts's own measurement) — there is no
 * in-memory copy of it to reuse the way there is for codex's roles.json-derived union.
 */
export function findUnregisteredRoleBindings(data: RolesFile, opts: FindUnregisteredRoleBindingsOptions = {}): string[] {
  const warnings: string[] = [];
  const codexRegistered = new Set(collectCodexRoleModelSlugsFromData(data));
  const qwenSnapshot = qwenSnapshotFor(opts.homeDir ?? homedir());
  for (const harness of CHECKED_HARNESSES) {
    for (const [profileName, profile] of Object.entries(data.profiles)) {
      const key = agentLookupKeys(harness).find((k) => k in profile.agents);
      if (!key) continue;
      const roles = profile.agents[key]?.roles ?? {};
      for (const [role, binding] of Object.entries(roles)) {
        const raw = binding.model?.trim();
        if (!raw || raw === "inherit") continue; // not a concrete id — nothing to check
        if (harness === "codex") {
          if (codexRegistered.has(raw)) continue;
          warnings.push(formatWarning({ profile: profileName, harness, role, model: raw }));
          continue;
        }
        // qwen
        if (!qwenSnapshot.ok) {
          warnings.push(formatUnverified({ profile: profileName, role, model: raw, reason: qwenSnapshot.reason }));
          continue;
        }
        const bare = bareModelId(raw);
        if (qwenSnapshot.ids.has(bare)) continue;
        warnings.push(formatWarning({ profile: profileName, harness, role, model: raw }));
      }
    }
  }
  return warnings;
}
