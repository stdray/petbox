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

export type TruthfulnessViolation = CapabilityViolation | ModelViolation;

export function isModelViolation(v: TruthfulnessViolation): v is ModelViolation {
  return "model" in v;
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
 */
export function checkRoleTruthfulness(
  role: AgentRole,
  harness: string,
  model?: string,
): readonly TruthfulnessViolation[] {
  const caps = harnessCapabilities(harness);
  const out: TruthfulnessViolation[] = [];
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
 */
export function checkTruthfulness(
  definition: AgentDefinition,
  harness: string,
  roleModels: Readonly<Record<string, string>> = {},
): readonly TruthfulnessViolation[] {
  const out: TruthfulnessViolation[] = [];
  for (const role of definition.roles) {
    out.push(...checkRoleTruthfulness(role, harness, roleModels[role.slug]));
  }
  return out;
}

/** Human-readable multi-line report (empty string when no violations). */
export function formatViolations(violations: readonly TruthfulnessViolation[]): string {
  if (violations.length === 0) return "";
  return violations
    .map((v) =>
      isModelViolation(v)
        ? `  role '${v.role}' is bound to model '${v.model}', which looks like ANOTHER harness's ` +
          `model id, not one harness '${v.harness}' would own — writing it would be either ` +
          `rejected loudly at runtime or silently satisfy a different harness's config, not ` +
          `this one. Known ${v.harness} aliases: ${v.allowedModels.join(", ")}. Fix the binding ` +
          `in ~/.petbox/roles.json (profile → agents.${v.harness}.roles.${v.role}.model).`
        : `  role '${v.role}' requires capability '${v.capability}' which harness '${v.harness}' does not declare`,
    )
    .join("\n");
}
