// Pure compile helpers for petbox-wire apply (per-harness-artifact).
//
// planApply produces agent role files for any known harness.
// Per-role truthfulness: clean roles are emitted; dirty roles are skipped and
// reported in violations/skippedRoles (never silently drop a required line from
// a role that is emitted — the whole dirty role is blocked). "Dirty" covers BOTH
// a missing harness capability AND a local model binding that looks like ANOTHER
// harness's id shape (harness-models.ts's "foreign" tier) — writing that in would be
// either rejected loudly by the target harness at runtime or silently satisfy a
// different harness's config, not this one. A model that is merely unrecognized-but-
// shape-valid (harness-models.ts's "unknown" tier — e.g. a real `claude-*` id newer
// than the kit's small known-alias list) is NOT dirty: it is written, with a warning
// (see ApplyPlan.warnings / modelShapeWarning in truthfulness.ts).
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
import { emittedRoleName, type AgentDefinition, type AgentRole } from "./agent-definition.ts";
import { isKnownHarness, type HarnessId } from "./harness-capabilities.ts";
import { PETBOX_MARKER_LINE } from "./origin-marker.ts";
import {
  checkRoleTruthfulness,
  formatViolations,
  modelShapeWarning,
  type TruthfulnessViolation,
} from "./truthfulness.ts";

/**
 * Factory custom-droid name sanitizer: lowercase/digits/-/_ only (Factory DroidValidator —
 * see renderDroidMarkdown below). Shared by renderDroidMarkdown (frontmatter `name:`) and
 * planApply (the emitted/legacy droid file basenames) so the two can never drift apart.
 */
export function sanitizeDroidName(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

export type PlannedFile = {
  readonly relativePath: string;
  readonly content: string;
  /**
   * Where this role's file lived BEFORE namespacing (bare `role.slug`, no `petbox-` prefix),
   * relative to the harness root — same convention as relativePath. apply's writer
   * (apply-write.ts's cleanupLegacyArtifact) uses this to find and remove an old file we own;
   * it is a no-op when nothing sits there, and it NEVER touches a path that lacks our origin
   * marker (chore: petbox-namespaced-agent-names). Always distinct from relativePath — every
   * role is namespaced now.
   */
  readonly legacyRelativePath: string;
};

export type ApplyPlan = {
  readonly harness: string;
  readonly files: readonly PlannedFile[];
  /** Truthfulness violations (capability or model) for roles that were NOT written. */
  readonly violations: readonly TruthfulnessViolation[];
  /** Role slugs skipped because of violations. */
  readonly skippedRoles: readonly string[];
  /**
   * Non-blocking notices, two kinds:
   *  - A role with NO local model binding is written without a model key (claude-code/opencode)
   *    or with `model: inherit` (droid) — legitimate, but it means the agent runs on the
   *    session/parent model. Warn so that is a choice, not a surprise.
   *  - A role bound to a model that classifies "unknown" (harness-models.ts) — shape-valid for
   *    the harness, just not on its small known-alias list — is written as bound, unverified.
   * A role bound to a "foreign"-shaped id is NOT a warning — it is a violation (see violations).
   */
  readonly warnings: readonly string[];
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
  lines.push(`# ${emittedRoleName(role)}`);
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

  // Both lists below NAME SPAWN/ESCALATION TARGETS — the literal subagent_type / handoff-role
  // string another role would use. They MUST render the same namespaced identity the target
  // role's own file is emitted under (emittedRoleName), never the bare internal role.slug —
  // otherwise a generated file would point at a subagent_type that does not exist on disk
  // (chore: petbox-namespaced-agent-names, "prose naming a spawn type renders the computed
  // name, never a constant/bare slug").
  lines.push("## Spawn");
  if (role.spawn?.allowed) {
    const allowed = role.spawn.allowedRoles?.length
      ? role.spawn.allowedRoles.map((r) => `\`${emittedRoleName(r)}\``).join(", ")
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
      ? role.escalation.targets.map((t) => `\`${emittedRoleName(t)}\``).join(", ")
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
 * name: <emittedRoleName(role)> is the required first key — without it Claude Code will not
 * register the file as an agent at all (mirrors renderDroidMarkdown's `name:`). Namespaced
 * (`petbox-<slug>`), not the bare role.slug — see agent-definition.ts's emittedRoleName.
 * model only when bound — never invent a model id.
 * No `tools:` key: omitting it means the agent inherits the harness's full tool
 * set, including MCP — that is the intended policy (see harness-capabilities.ts).
 * Every generated file carries PETBOX_MARKER_LINE in its frontmatter (origin-marker.ts) —
 * the ONLY thing apply's write guard trusts to tell "ours" from a real user file.
 */
export function renderAgentMarkdown(role: AgentRole, model?: string): string {
  const frontLines: string[] = [`name: ${emittedRoleName(role)}`];
  if (model && model.trim()) {
    frontLines.push(`model: ${model.trim()}`);
  }
  frontLines.push(`description: PetBox ${role.tier} role (${emittedRoleName(role)})`);
  frontLines.push(PETBOX_MARKER_LINE);

  const body = buildRoleBody(role);
  return `---\n${frontLines.join("\n")}\n---\n\n${body.endsWith("\n") ? body : body + "\n"}`;
}

/**
 * Factory custom droid markdown.
 * Docs: https://docs.factory.ai/cli/configuration/custom-droids
 * Frontmatter: name (required), description, model (inherit | explicit), optional
 * reasoningEffort / tools / mcpServers. Body = system prompt.
 * name: sanitizeDroidName(emittedRoleName(role)) — namespaced first, THEN sanitized, so the
 * `petbox-` prefix survives Factory's DroidValidator (`[a-z0-9_-]` only) the same way it
 * does on claude-code/opencode.
 * model: bound value from roles.json, else `inherit` (Factory default — not an invented id).
 * mcpServers: petbox when the role requires MCP (main or subagent surface).
 * Carries PETBOX_MARKER_LINE, same as renderAgentMarkdown — see origin-marker.ts.
 */
export function renderDroidMarkdown(
  role: AgentRole,
  model?: string,
  opts?: { mcpServerName?: string },
): string {
  const name = sanitizeDroidName(emittedRoleName(role));
  if (!name) {
    throw new Error(`role '${role.slug}': cannot form a valid droid name`);
  }

  const description = (
    role.notes?.trim() || `PetBox ${role.tier} role (${emittedRoleName(role)})`
  ).slice(0, 500);
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
  front.push(PETBOX_MARKER_LINE);

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
      warnings: [],
    };
  }

  const dir = agentFilesDir(harness);
  const files: PlannedFile[] = [];
  const violations: TruthfulnessViolation[] = [];
  const skippedRoles: string[] = [];
  const warnings: string[] = [];

  for (const role of definition.roles) {
    const model = roleModels[role.slug];
    // Gate BEFORE render: a role bound to a "foreign"-shaped model id is blocked, exactly like
    // a missing capability — never write an id that belongs to a different harness's config.
    const roleViolations = checkRoleTruthfulness(role, harness, model);
    if (roleViolations.length > 0) {
      violations.push(...roleViolations);
      skippedRoles.push(role.slug);
      continue;
    }
    if (!model || !model.trim()) {
      warnings.push(
        `role '${role.slug}' has no model binding for harness '${harness}' — the agent will ` +
          `inherit the session/parent model. Bind it in ~/.petbox/roles.json to pin a tier.`,
      );
    } else {
      const shapeWarning = modelShapeWarning(role, harness, model);
      if (shapeWarning) warnings.push(shapeWarning);
    }
    const fileName =
      harness === "droid" ? `${sanitizeDroidName(emittedRoleName(role))}.md` : `${emittedRoleName(role)}.md`;
    // Pre-namespacing name — same file this role used to emit before petbox-namespaced-agent-names.
    // The writer uses this to find + remove an owned leftover (never a foreign file at that path).
    const legacyFileName = harness === "droid" ? `${sanitizeDroidName(role.slug)}.md` : `${role.slug}.md`;
    files.push({
      relativePath: join(dir, fileName).replace(/\\/g, "/"),
      legacyRelativePath: join(dir, legacyFileName).replace(/\\/g, "/"),
      content: renderForHarness(harness, role, model),
    });
  }

  return { harness, files, violations, skippedRoles, warnings };
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
