// Definition truthfulness gate (definition-truthfulness).
//
// Given a portable agent definition + harness id, list every (role, capability)
// the role requires that the harness does not declare. Empty list = OK.
//
// NEVER silently drop a required capability — callers must fail loud when the
// list is non-empty (doctor / apply / tests).
//
// Plain TS for native node type-stripping: zero deps.

import type { AgentDefinition } from "./agent-definition.ts";
import { harnessCapabilities } from "./harness-capabilities.ts";

export type TruthfulnessViolation = {
  readonly role: string;
  readonly capability: string;
  readonly harness: string;
};

/**
 * Pure gate: definition + harness → violations (or empty).
 * Unknown harness ids declare zero capabilities → every required cap is a violation.
 */
export function checkTruthfulness(
  definition: AgentDefinition,
  harness: string,
): readonly TruthfulnessViolation[] {
  const caps = harnessCapabilities(harness);
  const out: TruthfulnessViolation[] = [];
  for (const role of definition.roles) {
    for (const capability of role.requiredCapabilities) {
      if (!caps.has(capability)) {
        out.push({ role: role.slug, capability, harness });
      }
    }
  }
  return out;
}

/** Human-readable multi-line report (empty string when no violations). */
export function formatViolations(violations: readonly TruthfulnessViolation[]): string {
  if (violations.length === 0) return "";
  return violations
    .map(
      (v) =>
        `  role '${v.role}' requires capability '${v.capability}' which harness '${v.harness}' does not declare`,
    )
    .join("\n");
}
