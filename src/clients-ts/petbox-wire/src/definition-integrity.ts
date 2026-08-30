// Cross-reference integrity of a RESOLVED agent definition (spec: definition-truthfulness).
//
// The other half of "an artifact never prescribes the impossible". truthfulness.ts answers
// "does this role need a capability the target harness does not declare"; this module answers
// the question nobody asked before (bug: artifact-integrity-dangling-and-orphans): does this
// role's rendered prose NAME A ROLE THAT DOES NOT EXIST?
//
// `spawn.allowedRoles` and `escalation.targets` were only ever parsed (agent-def-fetch.ts) and
// rendered (apply-artifacts.ts's buildRoleBody) — never once checked against the roster they
// name. A dangling target is an artifact that tells an agent to spawn a `subagent_type` which
// is not on disk, or to escalate to a role that is not there. It was harmless only because the
// roster never changed; the moment a layer (or a server edit) can SUBTRACT a role, every such
// reference becomes a live instruction to do something impossible.
//
// Scope, deliberately narrow — WHAT IS RENDERED, not what is merely stored:
//   - spawn targets are checked only when `spawn.allowed` is true;
//   - escalation targets are checked only when `escalation.available` is true.
// buildRoleBody prints "Not allowed."/"Not available." in the other cases and never names a
// single target, so a stale target list behind a disabled switch prescribes nothing and must
// not fail a build. This is the one deliberate narrowing versus the research prototype
// (research/wire-source-of-truth/prototype/resolve.mjs), whose E1 check is unconditional.
//
// Error code E1 and the `<role>.<path> → "<target>"` message shape come from that prototype,
// so its RESOLVED.md walkthrough and this implementation name the same defect the same way.
//
// Plain TS for native node type-stripping: zero deps.

import type { AgentDefinition } from "./agent-definition.ts";

export type DanglingTarget = {
  /** Role whose artifact would carry the impossible instruction. */
  readonly role: string;
  /** Which rendered list names it. */
  readonly field: "spawn.allowedRoles" | "escalation.targets";
  /** The named role that is not in the definition. */
  readonly target: string;
};

/**
 * Every rendered spawn/escalation target that names a role absent from `definition.roles`.
 * Pure, allocation-cheap, never throws — an empty array means the definition is referentially
 * closed. Order is stable: definition role order, then declaration order within a role.
 */
export function findDanglingTargets(definition: AgentDefinition): DanglingTarget[] {
  const known = new Set(definition.roles.map((r) => r.slug));
  const found: DanglingTarget[] = [];
  for (const role of definition.roles) {
    if (role.spawn?.allowed) {
      for (const target of role.spawn.allowedRoles ?? []) {
        if (!known.has(target)) {
          found.push({ role: role.slug, field: "spawn.allowedRoles", target });
        }
      }
    }
    if (role.escalation?.available) {
      for (const target of role.escalation.targets ?? []) {
        if (!known.has(target)) {
          found.push({ role: role.slug, field: "escalation.targets", target });
        }
      }
    }
  }
  return found;
}

/** One `E1 <role>.<field> → "<target>": ...` line per dangling reference (prototype shape). */
export function formatDanglingTargets(dangling: readonly DanglingTarget[]): string {
  return dangling
    .map(
      (d) =>
        `  E1 ${d.role}.${d.field} → "${d.target}": no such role in this definition — the ` +
        `artifact would prescribe a target that does not exist`,
    )
    .join("\n");
}
