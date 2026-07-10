// Pure compile helpers for petbox-wire apply (per-harness-artifact).
//
// First slice: opencode role files only (.opencode/agent/<role>.md).
// Claude Code / droid generators are non-goals for this slice.
//
// Body = role-specific text from the definition (spawn / escalation / caps /
// notes). Shared protocol inject stays in protocol.ts — not duplicated here.
// model: frontmatter only when a local binding supplies it (never invent).
//
// Plain TS for native node type-stripping: zero deps.

import { join } from "node:path";
import type { AgentDefinition, AgentRole } from "./agent-definition.ts";
import { checkTruthfulness, formatViolations, type TruthfulnessViolation } from "./truthfulness.ts";

export type PlannedFile = {
  readonly relativePath: string;
  readonly content: string;
};

export type ApplyPlan = {
  readonly harness: string;
  readonly files: readonly PlannedFile[];
  readonly violations: readonly TruthfulnessViolation[];
};

/** Render one opencode agent markdown file (YAML frontmatter + body). */
export function renderOpencodeAgentMarkdown(role: AgentRole, model?: string): string {
  const frontLines: string[] = [];
  if (model && model.trim()) {
    frontLines.push(`model: ${model.trim()}`);
  }
  // description helps opencode's agent picker; keep short and derived from tier/notes.
  frontLines.push(`description: PetBox ${role.tier} role (${role.slug})`);

  const body = buildRoleBody(role);
  if (frontLines.length === 0) {
    return body.endsWith("\n") ? body : body + "\n";
  }
  return `---\n${frontLines.join("\n")}\n---\n\n${body.endsWith("\n") ? body : body + "\n"}`;
}

function buildRoleBody(role: AgentRole): string {
  const lines: string[] = [];
  lines.push(`# ${role.slug}`);
  lines.push("");
  lines.push(`Tier: \`${role.tier}\``);
  lines.push("");

  if (role.notes) {
    lines.push(role.notes);
    lines.push("");
  }

  const caps = role.requiredCapabilities;
  lines.push("## Required capabilities");
  if (caps.length === 0) {
    lines.push("- (none declared — role is harness-portable with no capability gate)");
  } else {
    for (const c of caps) lines.push(`- \`${c}\``);
  }
  lines.push("");

  lines.push("## Spawn");
  if (role.spawn?.allowed) {
    const allowed = role.spawn.allowedRoles?.length
      ? role.spawn.allowedRoles.map((r) => `\`${r}\``).join(", ")
      : "(any)";
    lines.push(`- Allowed. Target roles: ${allowed}.`);
  } else {
    lines.push("- Not allowed (leaf role).");
  }
  lines.push("");

  lines.push("## Escalation");
  if (role.escalation?.available) {
    const targets = role.escalation.targets?.length
      ? role.escalation.targets.map((t) => `\`${t}\``).join(", ")
      : "(unspecified)";
    lines.push(`- Available → ${targets}.`);
  } else {
    lines.push("- Not available.");
  }
  lines.push("");

  // Explicit anti-lie for explore: never claim inheritance is globally forbidden.
  if (role.slug === "explore") {
    lines.push("## Model inheritance");
    lines.push(
      "On harnesses that declare `builtin_explore_inherits_model`, inheritance is the harness default. " +
        "Do not treat inheritance as a protocol violation for this role.",
    );
    lines.push("");
  }

  return lines.join("\n");
}

/**
 * Plan opencode artifact writes for a definition + optional role→model map.
 * Does not touch the filesystem. Violations are returned (never dropped);
 * callers must refuse to write when violations.length > 0.
 */
export function planOpencodeApply(
  definition: AgentDefinition,
  roleModels: Readonly<Record<string, string>> = {},
): ApplyPlan {
  const harness = "opencode";
  const violations = checkTruthfulness(definition, harness);
  if (violations.length > 0) {
    return { harness, files: [], violations };
  }

  const files: PlannedFile[] = definition.roles.map((role) => {
    const model = roleModels[role.slug];
    return {
      relativePath: join(".opencode", "agent", `${role.slug}.md`).replace(/\\/g, "/"),
      content: renderOpencodeAgentMarkdown(role, model),
    };
  });

  return { harness, files, violations: [] };
}

/** Loud multi-line error for CLI when apply is blocked by the gate. */
export function formatApplyBlocked(
  violations: readonly TruthfulnessViolation[],
  harness: string,
): string {
  return (
    `apply: truthfulness gate failed for harness '${harness}' — refusing to write artifacts:\n` +
    formatViolations(violations)
  );
}
