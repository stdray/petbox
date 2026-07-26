// Definition truthfulness gate (definition-truthfulness).
//
// Two claims are gated, with the SAME contract (list violations → callers fail loud,
// never silently emit a role that carries one):
//
//   1. capability — a role may only require capabilities the target harness declares
//      (harness-capabilities.ts).
//   2. model — a role's LOCAL model binding (~/.petbox/roles.json, passed in per role) is
//      three-tier classified against the target harness (harness-models.ts): known/unknown
//      never block, only a recognizably FOREIGN-harness id shape does (revised 2026-07-13,
//      task `model-gate-revision-premise-falsified`, after a live measurement disproved the
//      original premise — Claude Code does NOT silently inherit on an unresolvable frontmatter
//      model, it fails LOUD at runtime with an API error; the gate now exists to catch a
//      cross-harness id BEFORE that loud failure, not to prevent a silent one). Intake
//      `wire-apply-writes-unresolvable-model-id`, 2026-07-12: a droid id landed in the
//      claude-code block of roles.json.
//      An ABSENT binding is not a violation — that is the harness's documented inherit
//      behaviour and is surfaced as a warning by the caller, not a block. Neither is an
//      "unknown" model (shape-valid, just not on the small known-alias list) — that is also
//      surfaced as a warning, never a block (see modelShapeWarning below).
//
// NEVER silently drop a violation — callers must fail loud when the list is non-empty
// (doctor / apply / tests).
//
// Plain TS for native node type-stripping: zero deps.

import type { AgentDefinition, AgentRole } from "./agent-definition.ts";
import { harnessCapabilities } from "./harness-capabilities.ts";
import { allowedModels, classifyModel } from "./harness-models.ts";

export type CapabilityViolation = {
  readonly role: string;
  readonly capability: string;
  readonly harness: string;
};

export type ModelViolation = {
  readonly role: string;
  readonly harness: string;
  /** The unresolvable id the local binding asked us to write. */
  readonly model: string;
  /** Ids this harness can resolve (never empty — a model violation implies a closed policy). */
  readonly allowedModels: readonly string[];
};

/**
 * A role the definition actually declares has NO local model binding for a CLOSED-model-space
 * harness (harness-models.ts's allowedModels non-null — claude-code today). Produced ONLY by
 * apply's planApply (apply-artifacts.ts), and only for that closed subset of harnesses — NOT by
 * checkRoleTruthfulness / checkTruthfulness below, which stay exactly as documented: an absent
 * binding is the harness's legitimate inherit behaviour, not a violation (doctor still reports
 * it that way). apply is stricter here on purpose (reserve-unbound-inherits-session-model, owner
 * decision 2026-07-26): a role with no `model:` line silently rides the session/parent model,
 * which is exactly the 2026-07-26 fable→opus incident's shape made structural and permanent —
 * it would hit every fresh machine, on whichever role happens to be unbound, forever. apply now
 * refuses to write such a role at all instead of writing it with a warning — but ONLY where a
 * correct binding is actually knowable and verifiable (a closed alias/id space); an OPEN-space
 * harness (opencode, and droid absent its `inherit` seed) still gets the old warn-and-write
 * behavior, because the kit cannot name what "correct" would even look like there, and refusing
 * would punish the user for the kit's own ignorance (see apply-artifacts.ts's file header).
 */
export type UnboundViolation = {
  readonly role: string;
  readonly harness: string;
};

export type TruthfulnessViolation = CapabilityViolation | ModelViolation | UnboundViolation;

export function isModelViolation(v: TruthfulnessViolation): v is ModelViolation {
  return "model" in v;
}

export function isUnboundViolation(v: TruthfulnessViolation): v is UnboundViolation {
  return !("model" in v) && !("capability" in v);
}

/**
 * Effective required capabilities for a role.
 * spawn.allowed === true implicitly requires spawn_subagents so spawn prose cannot
 * bypass the capability gate by omitting it from requiredCapabilities.
 */
export function effectiveRequiredCapabilities(role: AgentRole): readonly string[] {
  const caps = [...role.requiredCapabilities];
  if (role.spawn?.allowed === true && !caps.includes("spawn_subagents")) {
    caps.push("spawn_subagents");
  }
  return caps;
}

/**
 * Pure model gate for one role + harness + its local binding.
 * Unbound (undefined / blank) → no violation (inherit is legitimate; caller warns).
 * Harness with an open model-id space → no violation (we make no claim).
 * Only the "foreign" tier (harness-models.ts) blocks — a recognizably different harness's id
 * shape (`custom:*`, `provider/model`) landing in this binding. The "unknown" tier (shape-valid
 * for this harness, just not on its small known-alias list) is NOT a violation — see
 * modelShapeWarning for its non-blocking notice.
 */
export function checkRoleModelTruthfulness(
  role: AgentRole,
  harness: string,
  model: string | undefined,
): readonly ModelViolation[] {
  const m = (model ?? "").trim();
  if (!m) return [];
  if (classifyModel(harness, m) !== "foreign") return [];
  return [
    {
      role: role.slug,
      harness,
      model: m,
      allowedModels: allowedModels(harness) ?? [],
    },
  ];
}

/**
 * Non-blocking notice for a role bound to a model that classifies "unknown": shape-valid for the
 * target harness (e.g. `claude-*`) but not on its small known-alias list — plausibly a real,
 * newer id this kit's list has not caught up with. Null when there is nothing to warn about
 * (unbound, "known", "foreign" — foreign is a violation, not a warning — or an open-policy
 * harness). Callers (apply-artifacts.ts) fold this into the same non-blocking warnings list used
 * for an unbound model.
 */
export function modelShapeWarning(
  role: AgentRole,
  harness: string,
  model: string | undefined,
): string | null {
  const m = (model ?? "").trim();
  if (!m) return null;
  if (classifyModel(harness, m) !== "unknown") return null;
  return (
    `role '${role.slug}' is bound to model '${m}' on harness '${harness}', which is not on the ` +
    `harness's known-alias list but matches its id shape — writing it unverified. If '${harness}' ` +
    `cannot actually resolve it, that fails LOUD at runtime (an API error), not silently.`
  );
}

/**
 * Pure gate for one role + harness (+ optional bound model) → violations (or empty).
 * Unknown harness ids declare zero capabilities → every required cap is a violation.
 * Return type deliberately excludes UnboundViolation: this is the SHARED gate doctor also uses,
 * and an absent binding stays legitimate inherit here (see UnboundViolation's doc comment) — it
 * never produces one. Widening this to TruthfulnessViolation would falsely suggest otherwise to
 * callers narrowing the result (e.g. `!isModelViolation(v) ⇒ CapabilityViolation`).
 */
export function checkRoleTruthfulness(
  role: AgentRole,
  harness: string,
  model?: string,
): readonly (CapabilityViolation | ModelViolation)[] {
  const caps = harnessCapabilities(harness);
  const out: (CapabilityViolation | ModelViolation)[] = [];
  for (const capability of effectiveRequiredCapabilities(role)) {
    if (!caps.has(capability)) {
      out.push({ role: role.slug, capability, harness });
    }
  }
  out.push(...checkRoleModelTruthfulness(role, harness, model));
  return out;
}

/**
 * Pure gate: definition + harness (+ role→model binding map) → all role violations (or empty).
 * Same UnboundViolation exclusion as checkRoleTruthfulness above — see its doc comment.
 */
export function checkTruthfulness(
  definition: AgentDefinition,
  harness: string,
  roleModels: Readonly<Record<string, string>> = {},
): readonly (CapabilityViolation | ModelViolation)[] {
  const out: (CapabilityViolation | ModelViolation)[] = [];
  for (const role of definition.roles) {
    out.push(...checkRoleTruthfulness(role, harness, roleModels[role.slug]));
  }
  return out;
}

/** Human-readable multi-line report (empty string when no violations). */
export function formatViolations(violations: readonly TruthfulnessViolation[]): string {
  if (violations.length === 0) return "";
  return violations
    .map((v) => {
      if (isModelViolation(v)) {
        return (
          `  role '${v.role}' is bound to model '${v.model}', which looks like ANOTHER harness's ` +
          `model id, not one harness '${v.harness}' would own — writing it would be either ` +
          `rejected loudly at runtime or silently satisfy a different harness's config, not ` +
          `this one. Known ${v.harness} aliases: ${v.allowedModels.join(", ")}. Fix the binding ` +
          `in ~/.petbox/roles.json (profile → agents.${v.harness}.roles.${v.role}.model).`
        );
      }
      // Checked by an inline `"capability" in v` rather than a second `isXViolation` guard: a
      // CapabilityViolation is structurally assignable to UnboundViolation's narrower shape
      // (both are just { role, harness } once you ignore extra fields), so two independent
      // user-defined type guards checked in sequence would let TS's negative narrowing collapse
      // the remaining branch to `never`. The `in` check on a specific literal key narrows
      // correctly instead.
      if ("capability" in v) {
        return `  role '${v.role}' requires capability '${v.capability}' which harness '${v.harness}' does not declare`;
      }
      return (
        `  role '${v.role}' has NO local model binding for harness '${v.harness}' — apply ` +
        `refuses to write it without one, so it can never silently ride the session/parent ` +
        `model. Bind it: \`petbox-wire model set ${v.role} <tier> --agent ${v.harness}\` ` +
        `(or edit ~/.petbox/roles.json directly: profile → agents.${v.harness}.roles.${v.role}.model).`
      );
    })
    .join("\n");
}
