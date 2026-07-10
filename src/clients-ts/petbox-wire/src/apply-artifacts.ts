// Pure compile helpers for petbox-wire apply (per-harness-artifact).
//
// planApply produces agent role files for any known harness. Violations for that
// harness → empty files + violations (never silent drop of required lines).
// model: frontmatter only when a local binding supplies it (never invent).
//
// Paths:
//   opencode     → .opencode/agent/<role>.md
//   claude-code  → .claude/agents/<role>.md  (plural agents)
//   droid        → .factory/agents/<role>.md
//
// Plain TS for native node type-stripping: zero deps.

import { join } from "node:path";
import type { AgentDefinition, AgentRole } from "./agent-definition.ts";
import { isKnownHarness, type HarnessId } from "./harness-capabilities.ts";
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

/** Relative dir (posix) for agent role files per harness. */
export function agentFilesDir(harness: HarnessId): string {
  switch (harness) {
    case "opencode":
      return ".opencode/agent";
    case "claude-code":
      return ".claude/agents";
    case "droid":
      return ".factory/agents";
  }
}

/** Shared role body (spawn / escalation / caps / notes). No protocol inject here. */
export function buildRoleBody(role: AgentRole): string {
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
 * Render one agent markdown file (YAML frontmatter + body).
 * model only when bound — never invent a model id.
 */
export function renderAgentMarkdown(role: AgentRole, model?: string): string {
  const frontLines: string[] = [];
  if (model && model.trim()) {
    frontLines.push(`model: ${model.trim()}`);
  }
  // description helps harness agent pickers; keep short and derived from tier/notes.
  frontLines.push(`description: PetBox ${role.tier} role (${role.slug})`);

  const body = buildRoleBody(role);
  if (frontLines.length === 0) {
    return body.endsWith("\n") ? body : body + "\n";
  }
  return `---\n${frontLines.join("\n")}\n---\n\n${body.endsWith("\n") ? body : body + "\n"}`;
}

/** @deprecated alias — prefer renderAgentMarkdown */
export function renderOpencodeAgentMarkdown(role: AgentRole, model?: string): string {
  return renderAgentMarkdown(role, model);
}

/**
 * Plan artifact writes for a definition + harness + optional role→model map.
 * Does not touch the filesystem. Violations are returned (never dropped);
 * callers must refuse to write when violations.length > 0.
 */
export function planApply(
  definition: AgentDefinition,
  harness: string,
  roleModels: Readonly<Record<string, string>> = {},
): ApplyPlan {
  if (!isKnownHarness(harness)) {
    return {
      harness,
      files: [],
      violations: definition.roles.flatMap((role) =>
        role.requiredCapabilities.map((capability) => ({
          role: role.slug,
          capability: String(capability),
          harness,
        })),
      ),
    };
  }

  const violations = checkTruthfulness(definition, harness);
  if (violations.length > 0) {
    return { harness, files: [], violations };
  }

  const dir = agentFilesDir(harness);
  const files: PlannedFile[] = definition.roles.map((role) => {
    const model = roleModels[role.slug];
    return {
      relativePath: join(dir, `${role.slug}.md`).replace(/\\/g, "/"),
      content: renderAgentMarkdown(role, model),
    };
  });

  return { harness, files, violations: [] };
}

/** Thin wrapper: planApply(..., "opencode"). */
export function planOpencodeApply(
  definition: AgentDefinition,
  roleModels: Readonly<Record<string, string>> = {},
): ApplyPlan {
  return planApply(definition, "opencode", roleModels);
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
