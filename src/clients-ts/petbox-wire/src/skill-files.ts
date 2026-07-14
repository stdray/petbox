// Skill-template rendering + writing — split out of wire.ts (which runs main() at module top
// level and must never be imported by a test — see posix-env.ts's comment on the identical
// problem) so the per-skill substitution and multi-surface fanout stay testable.
//
// `petbox-methodology` is deliberately a THIN, project-agnostic pointer, unlike `petbox` and
// `petbox-agent-factory`: it must never bake in this repo's own methodology rules (preset
// `quartet`, the `spec_plan` gate, `ideaRef`/`specRef`, …), because a wired project may run a
// different preset, a hand-tuned custom instance, or no methodology at all. The live rules for
// THAT project are always fetched at runtime via `tasks_methodology_guide` — never hardcoded at
// wire time (see the template itself for the reasoning).

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

// Skill surfaces wire.ts writes rendered skill bodies into. opencode is intentionally absent: it
// discovers skills through its Claude-compatible path (`.claude/skills/…`), and a second
// same-name copy under `.opencode/skills/` would be a duplicate whose resolution opencode does
// not document. Droid reads its own `.factory/skills/` root (its compat path is
// `.agent/skills/`, NOT `.claude/skills/`), so it needs a dedicated copy.
export const SKILL_SURFACES: string[][] = [
  [".claude", "skills"], // Claude Code (native) + opencode (Claude-compatible discovery)
  [".factory", "skills"], // Factory Droid (native)
];

export type SkillTemplateSpec = {
  // Directory name — used BOTH as the template's subdir under templatesRoot AND as the target
  // skill folder name under every SKILL_SURFACES root (e.g. "petbox-methodology" reads
  // <templatesRoot>/petbox-methodology/SKILL.md and writes .claude/skills/petbox-methodology/SKILL.md).
  dir: string;
  // Whether the template uses the {{WORKSPACE}} placeholder (only `petbox` does, for its UI URL).
  needsWorkspace: boolean;
};

// Every skill wire.ts renders into a freshly-wired project (see writeSkillFiles / wire.ts step 7).
export const PROJECT_SKILLS: SkillTemplateSpec[] = [
  { dir: "petbox", needsWorkspace: true },
  { dir: "petbox-agent-factory", needsWorkspace: false },
  { dir: "petbox-methodology", needsWorkspace: false },
];

// Substitute {{PROJECT}} and {{WORKSPACE}}. Safe to call uniformly even for a template that has
// no {{WORKSPACE}} placeholder — replace() on a pattern with zero matches is a no-op.
export function renderSkillTemplate(tpl: string, project: string, workspace: string): string {
  return tpl.replace(/\{\{PROJECT\}\}/g, project).replace(/\{\{WORKSPACE\}\}/g, workspace);
}

// Render every PROJECT_SKILLS entry from templatesRoot and write it into every SKILL_SURFACES
// root under dir. Returns the absolute paths written, in write order, for the caller's log lines.
export function writeSkillFiles(
  dir: string,
  templatesRoot: string,
  project: string,
  workspace: string,
): string[] {
  const written: string[] = [];
  for (const spec of PROJECT_SKILLS) {
    const tpl = readFileSync(join(templatesRoot, spec.dir, "SKILL.md"), "utf8");
    const rendered = renderSkillTemplate(tpl, project, workspace);
    for (const surface of SKILL_SURFACES) {
      const skillPath = join(dir, ...surface, spec.dir, "SKILL.md");
      mkdirSync(dirname(skillPath), { recursive: true });
      writeFileSync(skillPath, rendered, "utf8");
      written.push(skillPath);
    }
  }
  return written;
}
