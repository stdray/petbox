// Local role→model binding store (~/.petbox/roles.json).
//
// Spec (role-model-binding-local, binding-not-server-authoritative):
//   - Active profile + per-agent role→model bindings live on the machine (owner axis = $HOME).
//   - Server may observe a stamp as session metadata later, but is NEVER the source of truth.
//   - All load/save/resolve paths are offline (no fetch).
//
// Plain TS for native node type-stripping: zero deps. Home is injectable so tests never touch
// the real ~/.petbox.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { deriveBindingProvider } from "./binding-provider.ts";
import { classifyModel } from "./harness-models.ts";
import { petboxDir } from "./petbox-dir.ts";
import { wireLog } from "./wire-log.ts";

/**
 * WHO put this binding here (task role-model-bindings-review-refactor, defects #1/#2).
 *
 * - `kit`  — this kit seeded it from its own defaults. The kit MAY update it when its defaults
 *            change (seedMissingRoleBindings does exactly that), because it is only overwriting
 *            its own past decision.
 * - `owner` — a human chose it (`model set`, or a hand-edit the migration could not attribute to
 *            any historical kit seed). NEVER rewritten by the kit, ever.
 *
 * Before this field existed the kit could not tell the two apart, so the only safe strategy was
 * "touch nothing" — and a changed default therefore never reached a machine that already had a
 * roles.json (defect #1, observed live: two profiles out of three stayed on retired models).
 */
export type BindingOrigin = "kit" | "owner";

export type RoleBinding = {
  /** The harness's OWN dialect, byte for byte — never a canonical cross-harness name. There is no
   * name valid in all five harnesses (wiki `imena-modeley-i-perenosimost-profilya-po-pyati-
   * harnessam`), and this file already is the correspondence table. */
  readonly model: string;
  readonly origin: BindingOrigin;
  /** Which subscription/registry serves `model`, derived from the harness's grammar by
   * binding-provider.ts — null when this harness's value genuinely cannot name one. Stored, not
   * recomputed on every read, so a hand-edit that contradicts the value is VISIBLE
   * (findBindingProviderInconsistencies) instead of silently normalized away. */
  readonly provider: string | null;
};

export type AgentRoles = {
  readonly roles: Readonly<Record<string, RoleBinding>>;
};

export type Profile = {
  readonly agents: Readonly<Record<string, AgentRoles>>;
};

/**
 * Current on-disk format of ~/.petbox/roles.json.
 *
 * 1 — the original shape: no `formatVersion` key at all, a binding is `{ model }` alone.
 * 2 — a binding is `{ model, origin, provider }` (see RoleBinding). Both new fields landed in ONE
 *     bump on purpose: they are two fields of the same type behind one parser, and two bumps with
 *     two migrations would cost more than one for no gain (handoff `handoff-new-session`,
 *     "Разбивка этапов").
 */
export const ROLES_FORMAT_VERSION = 2;

export type RolesFile = {
  /** Absent on disk ⇒ 1 (the pre-origin shape) — see ROLES_FORMAT_VERSION. */
  readonly formatVersion: number;
  readonly activeProfile: string;
  readonly profiles: Readonly<Record<string, Profile>>;
};

/** Best-effort observation stamp for a session push (client-side only until the server accepts it). */
export type ObservedBinding = {
  readonly profile: string;
  readonly agent: string;
  readonly roles: Readonly<Record<string, string>>;
};

const EMPTY: RolesFile = { formatVersion: ROLES_FORMAT_VERSION, activeProfile: "default", profiles: {} };

/**
 * Canonical agent ids used by session push / harness matrix (`droid-push-session` stamps
 * agent:"droid", HARNESS_IDS uses droid).
 * Aliases accepted in roles.json for the same bucket — lookup is alias-aware so a file
 * written as `factory-droid` still resolves when push asks for `droid`.
 */
export const CANONICAL_AGENT_IDS = ["claude-code", "opencode", "droid", "codex", "qwen"] as const;

const AGENT_ALIASES: Readonly<Record<string, string>> = {
  "factory-droid": "droid",
  factory: "droid",
  cc: "claude-code",
  claude: "claude-code",
};

/** Map any alias / known id to the canonical agent id; unknown strings pass through. */
export function canonicalAgentId(agent: string): string {
  const a = agent.trim();
  if (!a) return a;
  return AGENT_ALIASES[a] ?? a;
}

/** Keys to try when reading agents{} from roles.json for a requested agent id. */
export function agentLookupKeys(agent: string): readonly string[] {
  const canon = canonicalAgentId(agent);
  const keys = new Set<string>([agent, canon]);
  for (const [alias, c] of Object.entries(AGENT_ALIASES)) {
    if (c === canon) keys.add(alias);
  }
  return [...keys];
}

export function rolesPath(homeDir: string = homedir()): string {
  return join(petboxDir(homeDir), "roles.json");
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function asBindingOrigin(v: unknown): BindingOrigin {
  // Anything else — including a v1 file, where the key does not exist — reads as `owner`, the
  // conservative value: the kit never rewrites an owner binding, so a wrong guess here can only
  // ever fail SAFE (a stale binding survives) and never destroy a real choice. Reclassifying a
  // v1 file's bindings is migrateRolesFile's job, and it is evidence-based, not a default.
  return v === "kit" ? "kit" : "owner";
}

function asModelBinding(v: unknown): RoleBinding | null {
  if (!isPlainObject(v)) return null;
  const model = v["model"];
  if (typeof model !== "string" || !model.trim()) return null;
  const providerRaw = v["provider"];
  const provider = typeof providerRaw === "string" && providerRaw.trim() ? providerRaw.trim() : null;
  return { model: model.trim(), origin: asBindingOrigin(v["origin"]), provider };
}

function asAgentRoles(v: unknown): AgentRoles | null {
  if (!isPlainObject(v)) return null;
  const rolesRaw = v["roles"];
  if (!isPlainObject(rolesRaw)) return { roles: {} };
  const roles: Record<string, RoleBinding> = {};
  for (const [role, binding] of Object.entries(rolesRaw)) {
    const b = asModelBinding(binding);
    if (b) roles[role] = b;
  }
  return { roles };
}

function asProfile(v: unknown): Profile {
  if (!isPlainObject(v)) return { agents: {} };
  const agentsRaw = v["agents"];
  if (!isPlainObject(agentsRaw)) return { agents: {} };
  const agents: Record<string, AgentRoles> = {};
  for (const [agent, ar] of Object.entries(agentsRaw)) {
    const parsed = asAgentRoles(ar);
    if (parsed) agents[agent] = parsed;
  }
  return { agents };
}

/** Light validation: coerce unknown JSON into a RolesFile; drop junk fields. */
export function normalizeRoles(raw: unknown): RolesFile {
  if (!isPlainObject(raw)) return { ...EMPTY };
  // A missing/garbage `formatVersion` is version 1 — the shape that predates it. Never defaulted
  // to the CURRENT version: that would claim a legacy file had already been migrated and skip the
  // one pass that can still tell a kit seed from an owner's choice.
  const rawVersion = raw["formatVersion"];
  const formatVersion = typeof rawVersion === "number" && Number.isFinite(rawVersion) ? rawVersion : 1;
  const activeProfile =
    typeof raw["activeProfile"] === "string" && raw["activeProfile"].trim()
      ? raw["activeProfile"].trim()
      : "default";
  const profilesRaw = raw["profiles"];
  if (!isPlainObject(profilesRaw)) return { formatVersion, activeProfile, profiles: {} };
  const profiles: Record<string, Profile> = {};
  for (const [name, p] of Object.entries(profilesRaw)) {
    if (!name.trim()) continue;
    profiles[name] = asProfile(p);
  }
  return { formatVersion, activeProfile, profiles };
}

export type LoadRolesOptions = {
  /**
   * `apply`'s polarity ONLY (bug: wire-silent-failures-invisible). A missing roles.json file is
   * always Class A (fresh machine, nothing bound yet) and never throws even in strict mode. But
   * a PRESENT file that fails to parse is exactly the 2026-07-12-incident shape: a corrupt
   * roles.json silently reading as "no bindings" made `apply` render every role with no
   * `model:` line, so every subagent inherited the session's model (the "worker rides on Opus"
   * incident). `apply` must hard-fail on that instead of compiling a falsely-empty roster;
   * everyone else (doctor, the `roles`/`model` CLI, session-push's best-effort read) stays on
   * the default non-strict behavior — silent, but with a Class-Б trace via wireLog.
   */
  readonly strict?: boolean;
};

/**
 * Load ~/.petbox/roles.json.
 * - Missing file → empty shell, always silent (Class A: nothing bound yet is the common case).
 * - Present but unparsable → non-strict: empty shell + a Class-Б line in ~/.petbox/wire.log
 *   (doctor surfaces it); strict (`apply` only): throws, so the caller hard-fails instead of
 *   silently compiling roles as if unbound.
 */
export function loadRoles(homeDir: string = homedir(), opts?: LoadRolesOptions): RolesFile {
  return loadRolesMigrated(homeDir, opts).data;
}

/**
 * `loadRoles` plus the format migration's REPORT — for the one caller that must print what the
 * migration did (wire.ts's seedDefaultRoleBindingsIfMissing). Everyone else uses `loadRoles`.
 *
 * The migration runs HERE, on every read, and not at some single explicit call site, for one
 * reason: any writer that saved a v1 file's contents back out before it was migrated would stamp
 * `origin: "owner"` on every binding (asBindingOrigin's conservative default) and permanently
 * destroy the evidence the migration needs — `profile use` alone would have been enough. Reading
 * through the migration makes that unexpressible: no code path can observe an un-migrated
 * RolesFile, so no code path can persist one. Idempotent by construction (a file already at
 * ROLES_FORMAT_VERSION is returned untouched, `migrations` empty), and the in-memory result is
 * identical whether or not the file on disk has been rewritten yet.
 */
export function loadRolesMigrated(
  homeDir: string = homedir(),
  opts?: LoadRolesOptions,
): { readonly data: RolesFile; readonly migrations: readonly RoleBindingMigration[] } {
  const path = rolesPath(homeDir);
  if (!existsSync(path)) return { data: { ...EMPTY, profiles: {} }, migrations: [] };
  try {
    const raw = JSON.parse(readFileSync(path, "utf8"));
    const migrated = migrateRolesFile(normalizeRoles(raw));
    return { data: migrated.data, migrations: migrated.migrations };
  } catch (e) {
    const detail = `roles.json at ${path} exists but failed to parse — ${e instanceof Error ? e.message : String(e)}`;
    if (opts?.strict) {
      throw new Error(
        `corrupt roles.json (${path}): ${e instanceof Error ? e.message : String(e)} — refusing to ` +
          `treat this as "no bindings" (the 2026-07-12 incident shape: a bad file silently reads as ` +
          `empty, apply then renders every role with no model: line, and every subagent inherits the ` +
          `session's model). Fix or remove the file and re-run apply.`,
      );
    }
    wireLog("roles", detail, homeDir);
    return { data: { ...EMPTY, profiles: {} }, migrations: [] };
  }
}

/** Persist roles.json (creates ~/.petbox if needed). */
export function saveRoles(data: RolesFile, homeDir: string = homedir()): void {
  const path = rolesPath(homeDir);
  mkdirSync(dirname(path), { recursive: true });
  const normalized = normalizeRoles(data);
  writeFileSync(path, JSON.stringify(normalized, null, 2) + "\n", "utf8");
}

/** True when there is no active profile shell and no agent role bindings at all. */
export function isEmptyRoles(data: RolesFile): boolean {
  const names = Object.keys(data.profiles);
  if (names.length === 0) return true;
  for (const p of Object.values(data.profiles)) {
    for (const a of Object.values(p.agents)) {
      if (Object.keys(a.roles).length > 0) return false;
    }
  }
  // Profiles may exist as empty shells (after `profile use`) — still "empty" for display
  // of bindings, but we still surface the active profile name.
  return Object.values(data.profiles).every((p) => Object.keys(p.agents).length === 0);
}

/**
 * Set activeProfile; create an empty profile shell if the name is new.
 * Returns the updated file (caller should saveRoles).
 */
export function useProfile(data: RolesFile, name: string): RolesFile {
  const n = name.trim();
  if (!n) throw new Error("profile name must be non-empty");
  const profiles: Record<string, Profile> = { ...data.profiles };
  if (!profiles[n]) profiles[n] = { agents: {} };
  return { formatVersion: data.formatVersion, activeProfile: n, profiles };
}

/** Role→model map for one agent under the active profile (missing → {}). Alias-aware. */
export function resolveAgentRoles(
  data: RolesFile,
  agent: string,
): Readonly<Record<string, string>> {
  const profile = data.profiles[data.activeProfile];
  if (!profile) return {};
  // Prefer exact key, then aliases / canonical (first non-empty wins).
  for (const key of agentLookupKeys(agent)) {
    const ar = profile.agents[key];
    if (!ar) continue;
    const out: Record<string, string> = {};
    for (const [role, b] of Object.entries(ar.roles)) out[role] = b.model;
    if (Object.keys(out).length > 0) return out;
  }
  return {};
}

/**
 * Pure client helper: observed binding stamp for session metadata.
 * Returns null when the active profile has no roles for this agent (do not invent defaults).
 * `agent` on the stamp is always the **canonical** id (e.g. droid, not factory-droid).
 */
export function resolveObservedBinding(
  agent: string,
  homeDir: string = homedir(),
): ObservedBinding | null {
  const data = loadRoles(homeDir);
  const roles = resolveAgentRoles(data, agent);
  if (Object.keys(roles).length === 0) return null;
  return {
    profile: data.activeProfile,
    agent: canonicalAgentId(agent),
    roles,
  };
}

/** Bootstrap-safe export shape (no secrets — roles.json has none). */
export function exportRolesBootstrap(data: RolesFile): RolesFile {
  return normalizeRoles(data);
}

export type SetRoleModelResult =
  | { readonly ok: true; readonly data: RolesFile; readonly warning?: string }
  | { readonly ok: false; readonly reason: string };

/**
 * Set one role's model binding for `agent` (alias-aware) under a profile (default: the
 * active one; created as an empty shell if new). This is `model set`'s core (spec
 * binding-set-by-tool) — the only writer of a role→model binding besides a text editor.
 *
 * Validated against the SAME three-tier policy `apply`/`doctor` already gate with
 * (harness-models.ts's classifyModel) — this function does not re-derive that policy, only
 * calls it. "foreign" (a recognizably different harness's id shape, e.g. droid's `custom:*`
 * landing in a claude-code binding — the 2026-07-12 incident shape) is refused unless
 * `allowUnknownModel` is set. "known" and "unknown" (shape-valid, just not on the small
 * known-alias list) both write; "unknown" (and a forced "foreign") come back with a
 * non-blocking `warning` for the caller to print (mirrors modelShapeWarning in truthfulness.ts).
 */
export function setRoleModel(
  data: RolesFile,
  opts: {
    readonly agent: string;
    readonly role: string;
    readonly model: string;
    readonly profile?: string;
    readonly allowUnknownModel?: boolean;
  },
): SetRoleModelResult {
  const canon = canonicalAgentId(opts.agent);
  const role = opts.role.trim();
  const model = opts.model.trim();
  if (!role) return { ok: false, reason: "role is required" };
  if (!model) {
    return {
      ok: false,
      reason: "model is required (use `model unset <role>` to clear a binding)",
    };
  }

  const cls = classifyModel(canon, model);
  if (cls === "foreign" && !opts.allowUnknownModel) {
    return {
      ok: false,
      reason:
        `model '${model}' looks like another harness's id shape, not one harness '${canon}' would ` +
        `own — refusing to write it (the 2026-07-12 incident shape: a foreign id landing in the ` +
        `wrong harness's binding). Pass --allow-unknown-model to force it through if you are certain.`,
    };
  }

  const profileName = opts.profile?.trim() || data.activeProfile;
  const profiles: Record<string, Profile> = { ...data.profiles };
  const existingProfile = profiles[profileName] ?? { agents: {} };
  const existingAgent = existingProfile.agents[canon] ?? { roles: {} };
  // origin: "owner" — this verb IS the owner deciding. From here the kit never rewrites this cell
  // again, no matter how its own defaults move (seedMissingRoleBindings only refreshes "kit"
  // bindings). That is the whole contract the origin field buys, and it is why `model set` needs
  // no separate "pin this" flag.
  const binding: RoleBinding = {
    model,
    origin: "owner",
    provider: deriveBindingProvider(canon, model).provider,
  };
  profiles[profileName] = {
    agents: {
      ...existingProfile.agents,
      [canon]: { roles: { ...existingAgent.roles, [role]: binding } },
    },
  };
  const next: RolesFile = {
    formatVersion: data.formatVersion,
    activeProfile: data.activeProfile,
    profiles,
  };

  if (cls === "foreign") {
    return {
      ok: true,
      data: next,
      warning:
        `model '${model}' does not match the id shape for harness '${canon}' — written anyway ` +
        `because --allow-unknown-model was passed. If '${canon}' cannot resolve it, that fails LOUD ` +
        `at runtime, not silently.`,
    };
  }
  if (cls === "unknown") {
    return {
      ok: true,
      data: next,
      warning:
        `model '${model}' is not on the known-alias list for harness '${canon}' but matches its id ` +
        `shape — written unverified. If '${canon}' cannot resolve it, that fails LOUD at runtime, ` +
        `not silently.`,
    };
  }
  return { ok: true, data: next };
}

export type UnsetRoleModelResult = {
  readonly data: RolesFile;
  readonly removed: boolean;
};

/**
 * Remove one role's model binding for `agent` (alias-aware) under a profile (default: the
 * active one). No-op-safe: an absent profile/agent/role returns `removed: false` and the
 * SAME `data` reference back, never throws. This is `model unset`'s core — a fair-empty
 * binding a role can hold on purpose (e.g. `reserve`, when the tester's machine lacks
 * access to the tier the role would otherwise be bound to).
 */
export function unsetRoleModel(
  data: RolesFile,
  opts: { readonly agent: string; readonly role: string; readonly profile?: string },
): UnsetRoleModelResult {
  const canon = canonicalAgentId(opts.agent);
  const role = opts.role.trim();
  const profileName = opts.profile?.trim() || data.activeProfile;
  const existingProfile = data.profiles[profileName];
  if (!existingProfile) return { data, removed: false };
  const existingAgent = existingProfile.agents[canon];
  if (!existingAgent || !(role in existingAgent.roles)) {
    return { data, removed: false };
  }
  const restRoles: Record<string, RoleBinding> = {};
  for (const [r, b] of Object.entries(existingAgent.roles)) {
    if (r !== role) restRoles[r] = b;
  }
  const profiles: Record<string, Profile> = {
    ...data.profiles,
    [profileName]: {
      agents: { ...existingProfile.agents, [canon]: { roles: restRoles } },
    },
  };
  return {
    data: { formatVersion: data.formatVersion, activeProfile: data.activeProfile, profiles },
    removed: true,
  };
}

// Default claude-code role→model seed for a BRAND-NEW machine (fresh-wire-roster-unusable):
// aliases only (never a concrete id — see harness-models.ts / the claude-api skill's live
// finding that the Task tool's `model` param is a closed enum of exactly these four tiers).
//
// `reserve` is bound too (reversing an earlier "leave it deliberately absent" call —
// reserve-unbound-inherits-session-model, owner decision 2026-07-26). It is bound to an ALIAS
// (`fable`), never a concrete model id: an id is exactly the foreign-shape mistake the
// 2026-07-12 incident was (a droid id landing in a claude-code binding) — an alias is
// harness-portable, and if the tier is genuinely unavailable on this machine it fails LOUD at
// spawn time (a closed Task-tool enum rejects it up front), never silently resolving to
// something else. It is bound to the STRONGEST tier on purpose, not a cautious default: the
// umbrella this card sits under (newcomer-equivalent-experience) means a newcomer's experience
// should match the owner's, and on the owner's own machine reserve already rides the strongest
// tier. reserve is also the one role where a weaker tier would defeat its own purpose — it is
// the second pair of eyes called in only once everything else has already been tried and failed,
// so it is never the role to economize on.
//
// Lives here (not wire.ts) so it has exactly ONE reader-safe source of truth: wire.ts's
// seedDefaultRoleBindingsIfMissing (the writer, on a fresh machine) AND status.ts's per-role
// model-source enumeration (a reader, on any machine) both need the identical map — wire.ts runs
// main() at module top level and must never be imported by a side module (see wire.ts's own file
// header), so a constant only wire.ts could see would force status.ts into either importing
// wire.ts (breaking that rule) or re-declaring a second copy that silently drifts from this one.
export const DEFAULT_ROLE_MODEL_SEED: Readonly<Record<string, string>> = {
  orchestrator: "opus",
  worker: "sonnet",
  "worker-highstakes": "opus",
  explore: "haiku",
  reserve: "fable",
};

// Default codex role->model seed for a brand-new machine (task wire-support-codex-qwen; REVISED
// 2026-09-08, owner decision: both codex and qwen run entirely on the DIRECT DeepSeek
// subscription until a routing proxy exists — Codex pins one `model_provider` per process,
// measured: a role file's `model_provider` field is accepted and silently DROPPED, so a per-role
// split across two subscriptions is impossible on this harness today). Unlike
// DEFAULT_ROLE_MODEL_SEED above, these are NOT aliases — codex's model policy is OPEN
// (harness-models.ts), so the kit cannot write a portable tier name; it writes the concrete
// provider slugs direct DeepSeek serves (live `GET https://api.deepseek.com/models`, 2026-09-08):
// exactly `deepseek-v4-pro`, `deepseek-v4-flash`, `deepseek-v4-flash-vision-exp`. There is no
// third family on this subscription, so `reserve` deliberately COLLAPSES onto `deepseek-v4-pro`
// — a known, accepted cost, not a bug to fix by inventing a substitute model.
export const CODEX_ROLE_MODEL_SEED: Readonly<Record<string, string>> = {
  orchestrator: "deepseek-v4-pro",
  worker: "deepseek-v4-flash",
  "worker-highstakes": "deepseek-v4-pro",
  explore: "deepseek-v4-flash",
  reserve: "deepseek-v4-pro",
};

// Default qwen role->model seed for a brand-new machine (task wire-support-codex-qwen; REVISED
// 2026-09-08, owner decision — see CODEX_ROLE_MODEL_SEED's comment for the "why both harnesses
// collapse onto the direct subscription" reasoning: identical bindings on both harnesses until a
// routing proxy exists). Qwen's model policy is OPEN (harness-models.ts), and the kit writes
// real modelProviders entries for these exact ids at user scope.
//
// A provider name can never appear in the `model:` selector itself — qwen matches the pre-colon
// segment against a closed auth-type enum and silently treats any unknown prefix as a bare model
// id (measured: `opencode-go:glm-5.3-flash` silently hit the wrong provider, exit 0, no warning)
// — so routing goes through the `deepseek` provider key under `providerProtocol`, exposed as an
// `openai`-keyed `modelProviders.deepseek` entry with a globally-unique decorated id (`ds-*`) and
// the true wire model name in `generationConfig.extra_body.model`. The `opencode-go` provider key
// stays registered (wire.ts's installGlobalHooks) — it documents the owner's second subscription
// and makes a future rebinding a config edit, not a rewrite — but no role binds through it today.
//
// orchestrator/worker-highstakes/reserve → `ds-deepseek-v4-pro`, worker/explore →
// `ds-deepseek-v4-flash` (mirrors CODEX_ROLE_MODEL_SEED's own pro/flash split; `reserve`
// collapses onto `ds-deepseek-v4-pro` for the same "no third family on this subscription" reason
// as codex's seed). Values are the `authType:model-id` form a role .md file's `model:` key
// requires (qwen-spec.md §5) — never a bare id, and `authType` here is always the literal
// `openai` (an auth TYPE, not a provider slug — see wire.ts's installGlobalHooks for why a
// custom prefix can never appear here).
export const QWEN_ROLE_MODEL_SEED: Readonly<Record<string, string>> = {
  orchestrator: "openai:ds-deepseek-v4-pro",
  worker: "openai:ds-deepseek-v4-flash",
  "worker-highstakes": "openai:ds-deepseek-v4-pro",
  explore: "openai:ds-deepseek-v4-flash",
  reserve: "openai:ds-deepseek-v4-pro",
};

// Per-harness role->model seed, one map per harness this kit knows how to seed automatically.
// `opencode` is DELIBERATELY absent — its model space is open/unknowable from the kit (see
// DEFAULT_ROLE_MODEL_SEED's doc comment above): it stays unbound and `apply` warns instead of
// seeding a made-up value. Single source of truth for BOTH seedMissingRoleBindings below (the
// existing-file case: bug harness-seed-skipped-when-roles-json-exists, a newly added HARNESS_IDS
// entry — codex/qwen — silently got zero bindings on any machine whose roles.json already held
// other profiles, because the old seeder only ever ran on a totally absent file) and wire.ts's
// seedDefaultRoleBindingsIfMissing (the totally-fresh-file case, which now also just reads this
// map instead of hand-building each harness's role set separately).
export const HARNESS_ROLE_MODEL_SEEDS: Readonly<Record<string, Readonly<Record<string, string>>>> = {
  "claude-code": DEFAULT_ROLE_MODEL_SEED,
  // droid's model space is open too, but Factory documents a real `inherit` frontmatter default
  // (https://docs.factory.ai/cli/configuration/custom-droids) — every role DEFAULT_ROLE_MODEL_SEED
  // knows gets that literal value, turning the implicit fallback into a visible binding.
  droid: Object.fromEntries(Object.keys(DEFAULT_ROLE_MODEL_SEED).map((role) => [role, "inherit"])),
  codex: CODEX_ROLE_MODEL_SEED,
  qwen: QWEN_ROLE_MODEL_SEED,
};

/**
 * Add the full seed role set for any harness that is COMPLETELY ABSENT from a profile's
 * `agents`, across EVERY profile in `data` — from HARNESS_ROLE_MODEL_SEEDS. Purely additive
 * (bug harness-seed-skipped-when-roles-json-exists — codex/qwen added to HARNESS_IDS after most
 * machines already had a roles.json, so the OLD seeder, which only ever ran on a totally absent
 * file, never gave them a single binding):
 *   - never overwrites a binding that is already present,
 *   - never touches a harness the seed map does not know about (opencode stays exactly as the
 *     user left it, present or not),
 *   - a harness that IS already present in a profile — even bound for only SOME of its roles —
 *     is left completely alone, roles included: a partial binding is the operator's own,
 *     deliberate or not, and `apply`'s unbound-role hard-refusal (planApply,
 *     reserve-unbound-inherits-session-model) is what is SUPPOSED to catch that gap and say so
 *     loudly, not have this seeder quietly paper over it. Seeding only ever fills in a harness
 *     that has NO entry at all.
 *   - alias-aware on the harness key: a profile already keyed by an alias (e.g. `factory-droid`)
 *     counts as "present" under that key, so no duplicate canonical-id entry appears.
 * Returns `changed: false` (and `data` back untouched, same reference) when every profile already
 * has an entry for every seeded harness — callers should skip the write entirely in that case, so
 * an already-fully-seeded roles.json is never even re-serialized.
 */
export function seedMissingRoleBindings(data: RolesFile): {
  data: RolesFile;
  changed: boolean;
  refreshed: readonly RoleBindingRefresh[];
  reattributed: readonly RoleBindingReattribution[];
} {
  let fileChanged = false;
  const refreshed: RoleBindingRefresh[] = [];
  const reattributed: RoleBindingReattribution[] = [];
  const profiles: Record<string, Profile> = {};
  for (const [profileName, profile] of Object.entries(data.profiles)) {
    const agents: Record<string, AgentRoles> = { ...profile.agents };
    let profileChanged = false;
    for (const [harness, seed] of Object.entries(HARNESS_ROLE_MODEL_SEEDS)) {
      const presentKey = agentLookupKeys(harness).find((k) => k in agents);
      if (presentKey === undefined) {
        const roles: Record<string, RoleBinding> = {};
        for (const [role, model] of Object.entries(seed)) roles[role] = kitBinding(harness, model);
        agents[harness] = { roles };
        profileChanged = true;
        continue;
      }
      // The harness IS present — refresh only the cells this kit itself seeded (origin "kit").
      // This is defect #1's actual fix: before the origin field the only safe move was to skip the
      // whole harness, so a changed default never reached a machine that already had a roles.json.
      // An "owner" cell is still skipped here, byte for byte, and a role the current seed no longer
      // knows is left alone rather than deleted (removing a binding is destructive and is not this
      // function's business).
      const existing = agents[presentKey];
      if (!existing) continue;
      const nextRoles: Record<string, RoleBinding> = { ...existing.roles };
      let agentChanged = false;
      for (const [role, binding] of Object.entries(existing.roles)) {
        if (binding.origin !== "kit") continue;
        // A cell LABELLED kit whose value is not one this kit has ever shipped was edited by hand
        // (a text editor, not `model set`, which stamps origin "owner" itself). Re-attribute it
        // and keep the value: the alternative is reverting an edit the operator made on purpose,
        // silently, on the next run — the exact failure mode this whole card exists to remove.
        // `origin` is therefore a cached label re-verified against the historical table on every
        // pass, never a claim taken on trust.
        if (!isHistoricalKitSeed(harness, role, binding.model)) {
          nextRoles[role] = makeRoleBinding(harness, binding.model, "owner");
          reattributed.push({ profile: profileName, agent: presentKey, role, model: binding.model });
          agentChanged = true;
          continue;
        }
        const current = seed[role];
        if (current === undefined || current === binding.model) continue;
        nextRoles[role] = kitBinding(harness, current);
        refreshed.push({
          profile: profileName,
          agent: presentKey,
          role,
          modelBefore: binding.model,
          modelAfter: current,
        });
        agentChanged = true;
      }
      if (agentChanged) {
        agents[presentKey] = { roles: nextRoles };
        profileChanged = true;
      }
    }
    profiles[profileName] = profileChanged ? { agents } : profile;
    if (profileChanged) fileChanged = true;
  }
  if (!fileChanged) return { data, changed: false, refreshed: [], reattributed: [] };
  return {
    data: { formatVersion: data.formatVersion, activeProfile: data.activeProfile, profiles },
    changed: true,
    refreshed,
    reattributed,
  };
}

/** A binding labelled `kit` whose value this kit never shipped — hand-edited, so it is the
 * owner's now and the label is corrected to say so. */
export type RoleBindingReattribution = {
  readonly profile: string;
  readonly agent: string;
  readonly role: string;
  readonly model: string;
};

/** One kit-origin binding the kit updated to its current default (seedMissingRoleBindings). */
export type RoleBindingRefresh = {
  readonly profile: string;
  readonly agent: string;
  readonly role: string;
  readonly modelBefore: string;
  readonly modelAfter: string;
};

/**
 * A complete RoleBinding for `model` on `harness`, with the provider DERIVED from the value by
 * binding-provider.ts — the exact shape every writer in this file produces.
 *
 * Exported so no caller can hand-build a binding that forgets a field or, worse, invents a
 * provider the value does not actually support: the whole point of the v2 format is that the
 * label beside a model is derived from that model, not typed in beside it.
 */
export function makeRoleBinding(
  harness: string,
  model: string,
  origin: BindingOrigin = "owner",
): RoleBinding {
  return { model, origin, provider: deriveBindingProvider(harness, model).provider };
}

/** A binding the kit is putting there itself: origin "kit", provider derived from the value. */
function kitBinding(harness: string, model: string): RoleBinding {
  return makeRoleBinding(harness, model, "kit");
}

// ---- slice operations: whole harness, one role across every harness, all profiles --------------
// (task role-model-bindings-review-refactor, stage D, defect #5: the matrix was only editable one
// CELL at a time — rebinding two harnesses across five roles cost ten `model set` calls). Every
// slice op below is built from the SAME single-cell primitives above (setRoleModel/unsetRoleModel)
// plus one new single-cell primitive (resetRoleModelToKitDefault) — no second validation path, no
// second CLI grammar: `model set`/`model unset` grow `--all-roles`/`--all-agents`/`--all-profiles`
// toggles beside their existing `--agent`/`--profile`, and `model reset` is the same shape.

/** The canonical role roster this kit's own seeds know about — what `--all-roles` walks by
 * default. Derived from DEFAULT_ROLE_MODEL_SEED (defined below) so it can never drift from the
 * actual seed; the constant itself is only usable after that seed exists, hence its placement
 * here rather than earlier in the file — see matrixRolesFor. */
function canonicalRoleIds(): readonly string[] {
  return Object.keys(DEFAULT_ROLE_MODEL_SEED);
}

/** One cell address in the profile×agent×role matrix, for reporting exactly what a slice op
 * touched (the card's "покажи, что именно изменится"). */
export type SliceCell = { readonly profile: string; readonly agent: string; readonly role: string };

/**
 * Resolve which roles a `--all-roles` slice touches for one (profile, agent) pair: the canonical
 * roster UNION whatever roles this agent already has bound in this profile — so a slice both
 * seeds the whole known roster on an agent that has none yet, AND still reaches a role the
 * operator added by hand that the canonical roster does not name. Only ever adds cells to the
 * slice, never removes one a plain reading of "this harness's roles" would expect.
 */
export function matrixRolesFor(data: RolesFile, profile: string, agent: string): readonly string[] {
  const canon = canonicalAgentId(agent);
  const roles = new Set<string>(canonicalRoleIds());
  const p = data.profiles[profile];
  if (p) {
    const key = agentLookupKeys(canon).find((k) => k in p.agents);
    if (key) for (const r of Object.keys(p.agents[key]?.roles ?? {})) roles.add(r);
  }
  return [...roles];
}

export type SliceSelector = {
  readonly profiles: "all" | readonly string[];
  readonly agents: "all" | readonly string[];
  readonly roles: "all" | readonly string[];
};

/** Expand a slice selector into the concrete (profile, agent, role) cells it addresses. The "all"
 * markers are resolved against `data`'s CURRENT shape (existing profiles; the canonical agent
 * roster; matrixRolesFor per agent) — never against a fixed guess, so a slice never reaches a
 * cell that could not otherwise exist and never touches a profile that does not exist yet. */
export function expandSlice(data: RolesFile, sel: SliceSelector): readonly SliceCell[] {
  const profiles = sel.profiles === "all" ? Object.keys(data.profiles) : sel.profiles;
  const cells: SliceCell[] = [];
  for (const profile of profiles) {
    const agents =
      sel.agents === "all" ? [...CANONICAL_AGENT_IDS] : sel.agents.map((a) => canonicalAgentId(a));
    for (const agent of agents) {
      const roles = sel.roles === "all" ? matrixRolesFor(data, profile, agent) : sel.roles;
      for (const role of roles) cells.push({ profile, agent, role });
    }
  }
  return cells;
}

export type SliceSetOutcome =
  | {
      readonly cell: SliceCell;
      readonly ok: true;
      readonly modelBefore: string | null;
      readonly modelAfter: string;
      readonly warning?: string;
    }
  | { readonly cell: SliceCell; readonly ok: false; readonly reason: string };

export type SetRoleModelSliceResult =
  | {
      readonly ok: true;
      readonly data: RolesFile;
      // Only successful outcomes ever reach this branch — the refusal check below returns
      // ok:false, with the full mixed outcome list, before this branch is ever constructed.
      readonly changes: readonly Extract<SliceSetOutcome, { readonly ok: true }>[];
    }
  | { readonly ok: false; readonly reason: string; readonly outcomes: readonly SliceSetOutcome[] };

/**
 * `model set`'s slice form: the SAME model written to every cell the selector expands to. Reuses
 * setRoleModel per cell — no second validation path. ALL-OR-NOTHING: if any cell in the slice
 * would be refused (a foreign id without --allow-unknown-model), the WHOLE slice is refused and
 * `data` comes back untouched — a bulk write is exactly the place a silent partial apply would be
 * hardest to notice, so pass 1 validates every cell against the ORIGINAL data before pass 2
 * commits any of them.
 */
export function setRoleModelSlice(
  data: RolesFile,
  sel: SliceSelector & { readonly model: string; readonly allowUnknownModel?: boolean },
): SetRoleModelSliceResult {
  const cells = expandSlice(data, sel);
  if (cells.length === 0) {
    return {
      ok: false,
      reason: "slice matched no cells (empty profile/agent/role selection)",
      outcomes: [],
    };
  }
  const outcomes: SliceSetOutcome[] = [];
  for (const cell of cells) {
    const r = setRoleModel(data, {
      agent: cell.agent,
      role: cell.role,
      model: sel.model,
      profile: cell.profile,
      ...(sel.allowUnknownModel !== undefined ? { allowUnknownModel: sel.allowUnknownModel } : {}),
    });
    if (!r.ok) {
      outcomes.push({ cell, ok: false, reason: r.reason });
      continue;
    }
    const before = data.profiles[cell.profile]?.agents[cell.agent]?.roles[cell.role]?.model ?? null;
    outcomes.push({ cell, ok: true, modelBefore: before, modelAfter: sel.model, ...(r.warning !== undefined ? { warning: r.warning } : {}) });
  }
  const refused = outcomes.filter((o): o is Extract<SliceSetOutcome, { ok: false }> => !o.ok);
  if (refused.length > 0) {
    return {
      ok: false,
      reason: `${refused.length} of ${cells.length} cell(s) in this slice were refused — nothing written`,
      outcomes,
    };
  }
  let current = data;
  for (const cell of cells) {
    const r = setRoleModel(current, {
      agent: cell.agent,
      role: cell.role,
      model: sel.model,
      profile: cell.profile,
      ...(sel.allowUnknownModel !== undefined ? { allowUnknownModel: sel.allowUnknownModel } : {}),
    });
    if (r.ok) current = r.data;
  }
  // Every outcome is ok:true here — refused.length was already checked above — this filter only
  // narrows the TYPE for the caller, it changes nothing about which entries are present.
  const applied = outcomes.filter((o): o is Extract<SliceSetOutcome, { readonly ok: true }> => o.ok);
  return { ok: true, data: current, changes: applied };
}

export type SliceUnsetOutcome = { readonly cell: SliceCell; readonly removed: boolean };
export type UnsetRoleModelSliceResult = {
  readonly data: RolesFile;
  readonly changes: readonly SliceUnsetOutcome[];
};

/** `model unset`'s slice form: removes every cell the selector expands to. No-op-safe per cell,
 * same as the single-cell primitive — an absent cell simply reports `removed: false`. */
export function unsetRoleModelSlice(data: RolesFile, sel: SliceSelector): UnsetRoleModelSliceResult {
  const cells = expandSlice(data, sel);
  let current = data;
  const changes: SliceUnsetOutcome[] = [];
  for (const cell of cells) {
    const r = unsetRoleModel(current, { agent: cell.agent, role: cell.role, profile: cell.profile });
    current = r.data;
    changes.push({ cell, removed: r.removed });
  }
  return { data: current, changes };
}

export type ResetRoleModelResult =
  | {
      readonly ok: true;
      readonly data: RolesFile;
      readonly modelBefore: string | null;
      readonly modelAfter: string;
    }
  | { readonly ok: false; readonly reason: string };

/**
 * Reset one role's binding for `agent` (alias-aware) to the kit's OWN current default — origin
 * "kit", UNCONDITIONALLY, even overwriting an "owner" binding. That override is the entire point
 * of an explicit reset: the operator is choosing to hand this cell back to the kit, which is a
 * different act from seedMissingRoleBindings only ever refreshing cells already labelled "kit".
 * Refuses (does not write) when this (harness, role) has no kit default at all — e.g. opencode,
 * or a role name the kit's seed does not know — because resetting to nothing would invent a
 * value, which is exactly what this file exists to never do.
 */
export function resetRoleModelToKitDefault(
  data: RolesFile,
  opts: { readonly agent: string; readonly role: string; readonly profile?: string },
): ResetRoleModelResult {
  const canon = canonicalAgentId(opts.agent);
  const role = opts.role.trim();
  const seedModel = HARNESS_ROLE_MODEL_SEEDS[canon]?.[role];
  if (seedModel === undefined) {
    return {
      ok: false,
      reason: `harness '${canon}' role '${role}' has no kit default to reset to (the kit has never seeded this cell)`,
    };
  }
  const profileName = opts.profile?.trim() || data.activeProfile;
  const profiles: Record<string, Profile> = { ...data.profiles };
  const existingProfile = profiles[profileName] ?? { agents: {} };
  const existingAgent = existingProfile.agents[canon] ?? { roles: {} };
  const before = existingAgent.roles[role]?.model ?? null;
  profiles[profileName] = {
    agents: {
      ...existingProfile.agents,
      [canon]: { roles: { ...existingAgent.roles, [role]: kitBinding(canon, seedModel) } },
    },
  };
  return {
    ok: true,
    data: { formatVersion: data.formatVersion, activeProfile: data.activeProfile, profiles },
    modelBefore: before,
    modelAfter: seedModel,
  };
}

export type SliceResetOutcome =
  | { readonly cell: SliceCell; readonly ok: true; readonly modelBefore: string | null; readonly modelAfter: string }
  | { readonly cell: SliceCell; readonly ok: false; readonly reason: string };

export type ResetRoleModelSliceResult = {
  readonly data: RolesFile;
  readonly changes: readonly SliceResetOutcome[];
};

/**
 * `model reset`'s slice form: every cell the selector expands to is reset to the kit's current
 * default, origin "kit" (resetRoleModelToKitDefault). A cell with no kit default is SKIPPED, not
 * an error for the whole slice: `--all-roles`/`--all-agents` deliberately walk a roster wider than
 * any one harness's seed covers (opencode has none at all), and a bulk reset must not refuse just
 * because part of its swept area is legitimately unseedable.
 */
export function resetRoleModelSlice(data: RolesFile, sel: SliceSelector): ResetRoleModelSliceResult {
  const cells = expandSlice(data, sel);
  let current = data;
  const changes: SliceResetOutcome[] = [];
  for (const cell of cells) {
    const r = resetRoleModelToKitDefault(current, { agent: cell.agent, role: cell.role, profile: cell.profile });
    if (r.ok) {
      current = r.data;
      changes.push({ cell, ok: true, modelBefore: r.modelBefore, modelAfter: r.modelAfter });
    } else {
      changes.push({ cell, ok: false, reason: r.reason });
    }
  }
  return { data: current, changes };
}

// ---- format migration: v1 (no origin/provider) -> v2 -------------------------------------------

// EVERY value this kit has EVER seeded, per harness and per role, oldest first.
//
// This is the migration's only evidence. A v1 binding whose value matches one of these BYTE FOR
// BYTE was put there by a past version of this kit and is therefore safe for the kit to update;
// anything else is the owner's and is never touched (handoff `handoff-new-session`, accepted
// migration heuristic). Matching is per (harness, ROLE) rather than per harness on purpose: the
// same string is a kit seed for one role and a deliberate owner choice for another
// (`deepseek-v4-pro` seeds codex/orchestrator but never codex/worker), and a per-harness match
// would rewrite the second.
//
// Sources — read out of git, not remembered (`git show <sha>:src/clients-ts/petbox-wire/src/roles.ts`,
// plus wire.ts for the era when the seed lived inline there):
//   claude-code  every commit since the seed existed: opus/sonnet/haiku/fable, unchanged. The
//                ROLE set moved (`utility` existed until the roster dropped it, `worker-highstakes`
//                arrived in 1373e225) — the values never did.
//   droid        the literal `inherit` for every seeded role, always (Factory's own documented
//                frontmatter default).
//   codex        introduced in c76b2be0 with reserve -> `grok-4.6`; 1ab01619 replaced it with
//                `deepseek-v4-pro` when both new harnesses collapsed onto the direct subscription.
//   qwen         introduced in c76b2be0 as undecorated `openai:<vendor-id>`; 1ab01619 replaced all
//                five with the kit-decorated `openai:ds-*` ids that its modelProviders entries
//                actually register.
//
// > Changing a seed above WITHOUT appending its old value here silently converts every machine
// > still holding it into "owner chose this", freezing it forever. HISTORY IS APPEND-ONLY.
// > `roles.test.ts` ratchets the half a test can check — every CURRENT seed value must appear
// > here — so a new value cannot be added without touching this table.
export const HISTORICAL_ROLE_MODEL_SEEDS: Readonly<
  Record<string, Readonly<Record<string, readonly string[]>>>
> = {
  "claude-code": {
    orchestrator: ["opus"],
    worker: ["sonnet"],
    "worker-highstakes": ["opus"],
    utility: ["haiku"],
    explore: ["haiku"],
    reserve: ["fable"],
  },
  droid: Object.fromEntries(
    ["orchestrator", "worker", "worker-highstakes", "utility", "explore", "reserve"].map((role) => [
      role,
      ["inherit"],
    ]),
  ),
  codex: {
    orchestrator: ["deepseek-v4-pro"],
    worker: ["deepseek-v4-flash"],
    "worker-highstakes": ["deepseek-v4-pro"],
    explore: ["deepseek-v4-flash"],
    reserve: ["grok-4.6", "deepseek-v4-pro"],
  },
  qwen: {
    orchestrator: ["openai:deepseek-v4-pro", "openai:ds-deepseek-v4-pro"],
    worker: ["openai:glm-5.3-flash", "openai:ds-deepseek-v4-flash"],
    "worker-highstakes": ["openai:deepseek-v4-pro", "openai:ds-deepseek-v4-pro"],
    explore: ["openai:glm-5.3-flash", "openai:ds-deepseek-v4-flash"],
    reserve: ["openai:qwen3.8-max", "openai:ds-deepseek-v4-pro"],
  },
  // `opencode` is absent because the kit has NEVER seeded it (its `provider/model` space has no
  // safe placeholder — see HARNESS_ROLE_MODEL_SEEDS). Absent here means every opencode binding on
  // every machine is, correctly, the owner's.
};

/** One cell the migration attributed and possibly rewrote. */
export type RoleBindingMigration = {
  readonly profile: string;
  readonly agent: string;
  readonly role: string;
  readonly origin: BindingOrigin;
  readonly modelBefore: string;
  readonly modelAfter: string;
  readonly provider: string | null;
};

/** True when `model` is byte-for-byte a value this kit has ever seeded for exactly this cell. */
export function isHistoricalKitSeed(harness: string, role: string, model: string): boolean {
  return (HISTORICAL_ROLE_MODEL_SEEDS[canonicalAgentId(harness)]?.[role] ?? []).includes(model);
}

/**
 * Bring a parsed roles.json up to ROLES_FORMAT_VERSION. Pure; never touches disk.
 *
 * For a v1 file, per binding:
 *   - value matches a historical kit seed for THIS harness+role  -> origin `kit`, and the value is
 *     updated to the kit's CURRENT default for that cell (this is what finally delivers a changed
 *     default to a machine that already had a roles.json — defect #1);
 *   - anything else -> origin `owner`, value untouched, byte for byte;
 *   - `provider` is derived from the (possibly updated) value by binding-provider.ts.
 *
 * A `kit` cell the current seed no longer covers keeps its value: the kit stopped seeding that
 * role, which is not a reason to unbind a role that is still declared.
 *
 * IDEMPOTENT: a file already at ROLES_FORMAT_VERSION is returned as-is, same reference, with an
 * empty report — so a second run changes nothing, and the report is never a lie about a no-op.
 */
export function migrateRolesFile(data: RolesFile): {
  readonly data: RolesFile;
  readonly changed: boolean;
  readonly migrations: readonly RoleBindingMigration[];
} {
  if (data.formatVersion >= ROLES_FORMAT_VERSION) {
    return { data, changed: false, migrations: [] };
  }
  const migrations: RoleBindingMigration[] = [];
  const profiles: Record<string, Profile> = {};
  for (const [profileName, profile] of Object.entries(data.profiles)) {
    const agents: Record<string, AgentRoles> = {};
    for (const [agentKey, agentRoles] of Object.entries(profile.agents)) {
      const canon = canonicalAgentId(agentKey);
      const roles: Record<string, RoleBinding> = {};
      for (const [role, binding] of Object.entries(agentRoles.roles)) {
        const fromKit = isHistoricalKitSeed(canon, role, binding.model);
        const origin: BindingOrigin = fromKit ? "kit" : "owner";
        const current = HARNESS_ROLE_MODEL_SEEDS[canon]?.[role];
        const model = fromKit && current !== undefined ? current : binding.model;
        const provider = deriveBindingProvider(canon, model).provider;
        roles[role] = { model, origin, provider };
        migrations.push({
          profile: profileName,
          agent: agentKey,
          role,
          origin,
          modelBefore: binding.model,
          modelAfter: model,
          provider,
        });
      }
      agents[agentKey] = { roles };
    }
    profiles[profileName] = { agents };
  }
  return {
    data: { formatVersion: ROLES_FORMAT_VERSION, activeProfile: data.activeProfile, profiles },
    changed: true,
    migrations,
  };
}

/** The subset of a migration report that actually changed a model value — what a human needs to
 * see, as opposed to the cells that were merely labelled. */
export function rewrittenByMigration(
  migrations: readonly RoleBindingMigration[],
): readonly RoleBindingMigration[] {
  return migrations.filter((m) => m.modelBefore !== m.modelAfter);
}

// ---- internal consistency: does the stored provider still match the value? --------------------

/**
 * Every binding whose STORED `provider` contradicts what its own `model` value parses to under its
 * harness's grammar, as ready-to-print lines. Empty on any file this kit wrote — the kit derives
 * the label from the value on every write — so a hit means the file was hand-edited into a state
 * where the label lies about the value.
 *
 * A derived `null` (this harness's value genuinely cannot name a provider) never contradicts a
 * stored label: that is the opencode/qwen "unknown id" case, where a label the operator added by
 * hand carries information the value does not.
 *
 * Deliberately NOT a check against live machine config (qwen's modelProviders, codex's /models) —
 * that is stage B2. This one is internal: value vs label, nothing else.
 */
export function findBindingProviderInconsistencies(data: RolesFile): string[] {
  const issues: string[] = [];
  for (const [profileName, profile] of Object.entries(data.profiles)) {
    for (const [agentKey, agentRoles] of Object.entries(profile.agents)) {
      const canon = canonicalAgentId(agentKey);
      for (const [role, binding] of Object.entries(agentRoles.roles)) {
        const derived = deriveBindingProvider(canon, binding.model);
        if (derived.provider === null) continue;
        if (binding.provider === derived.provider) continue;
        issues.push(
          `binding provider: profile '${profileName}' harness '${agentKey}' role '${role}' is ` +
            `labelled provider '${binding.provider ?? "(none)"}' but its model '${binding.model}' ` +
            `parses to '${derived.provider}' (${derived.reason}). The label is what a reader trusts ` +
            `to see which subscription serves this role; re-derive it with ` +
            `\`petbox-wire model set ${role} ${binding.model} --agent ${canon} --profile ${profileName}\`.`,
        );
      }
    }
  }
  return issues;
}

/** Human-readable dump of the active profile's agent/role/model tree. */
export function formatResolvedBinding(data: RolesFile): string {
  const lines: string[] = [];
  lines.push(`activeProfile: ${data.activeProfile}  (roles.json format v${data.formatVersion})`);
  const profile = data.profiles[data.activeProfile];
  if (!profile || Object.keys(profile.agents).length === 0) {
    lines.push("(no agent role bindings for this profile)");
    return lines.join("\n");
  }
  for (const [agent, ar] of Object.entries(profile.agents)) {
    lines.push(`  ${agent}:`);
    const roleEntries = Object.entries(ar.roles);
    if (roleEntries.length === 0) {
      lines.push("    (no roles)");
      continue;
    }
    for (const [role, binding] of roleEntries) {
      // origin and provider are the two things this file could NOT answer before (defects #2/#4):
      // who bound it, and which subscription pays for it. Printed on every line so `roles` answers
      // both without reading roles.json, let alone the kit's sources.
      const provider = binding.provider ?? "provider unknown";
      lines.push(`    ${role}: ${binding.model}  [${provider}, set by ${binding.origin}]`);
    }
  }
  return lines.join("\n");
}
