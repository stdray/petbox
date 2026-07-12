// Pure compile helpers for petbox-wire apply (per-harness-artifact).
//
// planApply produces agent role files for any known harness.
// Per-role truthfulness: clean roles are emitted; dirty roles are skipped and
// reported in violations/skippedRoles (never silently drop a required line from
// a role that is emitted — the whole dirty role is blocked).
//
// Paths (documented harness layouts):
//   opencode     → .opencode/agent/<role>.md
//   claude-code  → .claude/agents/<role>.md
//   droid        → .factory/droids/<name>.md  (Factory custom droids; project level)
//     https://docs.factory.ai/cli/configuration/custom-droids
//
// model: from local roles.json binding when present; droid unbound → model: inherit
// (Factory default). Never invent a concrete model id.
//
// Plain TS for native node type-stripping: zero deps.

import { join } from "node:path";
import type { AgentDefinition, AgentRole } from "./agent-definition.ts";
import { isKnownHarness, type HarnessId } from "./harness-capabilities.ts";
import {
  checkRoleTruthfulness,
  formatViolations,
  type TruthfulnessViolation,
} from "./truthfulness.ts";

export type PlannedFile = {
  readonly relativePath: string;
  readonly content: string;
};

export type ApplyPlan = {
  readonly harness: string;
  readonly files: readonly PlannedFile[];
  /** Truthfulness violations for roles that were NOT written. */
  readonly violations: readonly TruthfulnessViolation[];
  /** Role slugs skipped because of violations. */
  readonly skippedRoles: readonly string[];
};

/** Relative dir (posix) for agent role files per harness. */
export function agentFilesDir(harness: HarnessId): string {
  switch (harness) {
    case "opencode":
      return ".opencode/agent";
    case "claude-code":
      return ".claude/agents";
    case "droid":
      // Factory custom droids — project level only (org-locked settings are out of scope).
      // https://docs.factory.ai/cli/configuration/custom-droids
      return ".factory/droids";
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
    lines.push(
      "- Not allowed. This is a leaf role: you MUST NOT spawn subagents " +
        "(no Agent/Task/spawn tool use of any kind), regardless of what tools the " +
        "harness makes available to this session.",
    );
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
 * Claude Code / opencode agent markdown (YAML frontmatter + body).
 * name: <role.slug> is the required first key — without it Claude Code will not
 * register the file as an agent at all (mirrors renderDroidMarkdown's `name:`).
 * model only when bound — never invent a model id.
 * No `tools:` key: omitting it means the agent inherits the harness's full tool
 * set, including MCP — that is the intended policy (see harness-capabilities.ts).
 */
export function renderAgentMarkdown(role: AgentRole, model?: string): string {
  const frontLines: string[] = [`name: ${role.slug}`];
  if (model && model.trim()) {
    frontLines.push(`model: ${model.trim()}`);
  }
  frontLines.push(`description: PetBox ${role.tier} role (${role.slug})`);

  const body = buildRoleBody(role);
  return `---\n${frontLines.join("\n")}\n---\n\n${body.endsWith("\n") ? body : body + "\n"}`;
}

/**
 * Factory custom droid markdown.
 * Docs: https://docs.factory.ai/cli/configuration/custom-droids
 * Frontmatter: name (required), description, model (inherit | explicit), optional
 * reasoningEffort / tools / mcpServers. Body = system prompt.
 * model: bound value from roles.json, else `inherit` (Factory default — not an invented id).
 * mcpServers: petbox when the role requires MCP (main or subagent surface).
 */
export function renderDroidMarkdown(
  role: AgentRole,
  model?: string,
  opts?: { mcpServerName?: string },
): string {
  // name: lowercase/digits/-/_ only (Factory DroidValidator).
  const name = role.slug
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, "-")
    .replace(/^-+|-+$/g, "");
  if (!name) {
    throw new Error(`role '${role.slug}': cannot form a valid droid name`);
  }

  const description = (role.notes?.trim() || `PetBox ${role.tier} role (${role.slug})`).slice(
    0,
    500,
  );
  const modelLine =
    model && model.trim() ? model.trim() : "inherit"; // Factory default when unbound

  const front: string[] = [
    `name: ${name}`,
    `description: ${JSON.stringify(description)}`,
    `model: ${modelLine}`,
  ];

  // Scope petbox MCP to roles that need it (mcpServers = configured server names).
  const needsMcp =
    role.requiredCapabilities.includes("mcp_main_session") ||
    role.requiredCapabilities.includes("mcp_subagent") ||
    role.spawn?.allowed === true;
  if (needsMcp) {
    const server = opts?.mcpServerName ?? "petbox";
    front.push(`mcpServers: ["${server}"]`);
  }

  const body = buildRoleBody(role);
  return `---\n${front.join("\n")}\n---\n\n${body.endsWith("\n") ? body : body + "\n"}`;
}

/** @deprecated alias — prefer renderAgentMarkdown */
export function renderOpencodeAgentMarkdown(role: AgentRole, model?: string): string {
  return renderAgentMarkdown(role, model);
}

function renderForHarness(
  harness: HarnessId,
  role: AgentRole,
  model: string | undefined,
): string {
  if (harness === "droid") return renderDroidMarkdown(role, model);
  return renderAgentMarkdown(role, model);
}

/**
 * Plan artifact writes for a definition + harness + optional role→model map.
 * Emits clean roles; skips dirty roles with violations reported (not silent).
 * Does not touch the filesystem.
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
      skippedRoles: definition.roles.map((r) => r.slug),
    };
  }

  const dir = agentFilesDir(harness);
  const files: PlannedFile[] = [];
  const violations: TruthfulnessViolation[] = [];
  const skippedRoles: string[] = [];

  for (const role of definition.roles) {
    const roleViolations = checkRoleTruthfulness(role, harness);
    if (roleViolations.length > 0) {
      violations.push(...roleViolations);
      skippedRoles.push(role.slug);
      continue;
    }
    const model = roleModels[role.slug];
    const fileName = harness === "droid" ? `${role.slug.toLowerCase().replace(/[^a-z0-9_-]+/g, "-")}.md` : `${role.slug}.md`;
    files.push({
      relativePath: join(dir, fileName).replace(/\\/g, "/"),
      content: renderForHarness(harness, role, model),
    });
  }

  return { harness, files, violations, skippedRoles };
}

/** Thin wrapper: planApply(..., "opencode"). */
export function planOpencodeApply(
  definition: AgentDefinition,
  roleModels: Readonly<Record<string, string>> = {},
): ApplyPlan {
  return planApply(definition, "opencode", roleModels);
}

/** Loud multi-line error for CLI when roles are blocked by the gate. */
export function formatApplyBlocked(
  violations: readonly TruthfulnessViolation[],
  harness: string,
  skippedRoles?: readonly string[],
): string {
  const skip =
    skippedRoles && skippedRoles.length > 0
      ? `\n  skipped roles: ${skippedRoles.join(", ")}`
      : "";
  return (
    `apply: truthfulness gate blocked role(s) on harness '${harness}':\n` +
    formatViolations(violations) +
    skip
  );
}
