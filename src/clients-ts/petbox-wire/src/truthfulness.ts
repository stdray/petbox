// Definition truthfulness gate (definition-truthfulness).
//
// Two claims are gated, with the SAME contract (list violations → callers fail loud,
// never silently emit a role that carries one):
//
//   1. capability — a role may only require capabilities the target harness declares
//      (harness-capabilities.ts).
//   2. model — a role's LOCAL model binding (~/.petbox/roles.json, passed in per role) must
//      be an id the target harness can actually resolve (harness-models.ts). Claude Code does
//      not error on an unresolvable frontmatter model — it silently inherits the session's
//      model, so a wrong id is invisible at runtime (intake
//      `wire-apply-writes-unresolvable-model-id`: workers rode Opus for a session).
//      An ABSENT binding is not a violation — that is the harness's documented inherit
//      behaviour and is surfaced as a warning by the caller, not a block.
//
// NEVER silently drop a violation — callers must fail loud when the list is non-empty
// (doctor / apply / tests).
//
// Plain TS for native node type-stripping: zero deps.

import type { AgentDefinition, AgentRole } from "./agent-definition.ts";
import { harnessCapabilities } from "./harness-capabilities.ts";
import { allowedModels, isResolvableModel } from "./harness-models.ts";

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
 */
export function checkRoleModelTruthfulness(
  role: AgentRole,
  harness: string,
  model: string | undefined,
): readonly ModelViolation[] {
  const m = (model ?? "").trim();
  if (!m) return [];
  if (isResolvableModel(harness, m)) return [];
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
        ? `  role '${v.role}' is bound to model '${v.model}' which harness '${v.harness}' ` +
          `cannot resolve — it would SILENTLY inherit the session model. ` +
          `Allowed: ${v.allowedModels.join(", ")}. Fix the binding in ~/.petbox/roles.json ` +
          `(profile → agents.${v.harness}.roles.${v.role}.model).`
        : `  role '${v.role}' requires capability '${v.capability}' which harness '${v.harness}' does not declare`,
    )
    .join("\n");
}
