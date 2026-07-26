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
//
// Origin marker (bug: skill-files-clobber-and-apply-skips): this module used to `writeFileSync`
// unconditionally — no existence check, no marker, no refusal — the exact class `apply-write.ts`
// already closed for `.claude/agents/*.md`. A user's own hand-edited `SKILL.md` was silently
// destroyed on the next `wire`. All three templates now carry `petbox: managed` in their
// frontmatter (origin-marker.ts), and every write goes through `writeArtifact` (apply-write.ts):
// new path → write; marked existing path → overwrite ("own"); unmarked existing path → refused
// ("blocked"), UNLESS it is byte-for-byte identical to what the PRE-marker template would have
// rendered, in which case it is a leftover from before this fix and gets promoted ("migrated") —
// without that carve-out, the very first `wire`/`apply` after this fix would block on every
// skill file the owner already has on disk.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { writeArtifact } from "./apply-write.ts";
import { hasPetboxMarker, PETBOX_MARKER_LINE } from "./origin-marker.ts";

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

// What the PRE-marker template used to render, byte-for-byte, for the migration carve-out below.
// The marker is the ONLY thing added to the templates by this fix (see module comment), so
// stripping its exact line back out of a freshly rendered body reconstructs the legacy output —
// no separate "old template" copy to keep in sync.
const MARKER_LINE_WITH_EOL = new RegExp(`^${PETBOX_MARKER_LINE}\\r?\\n`, "m");
function stripMarkerLine(rendered: string): string {
  return rendered.replace(MARKER_LINE_WITH_EOL, "");
}

export type SkillWriteOutcome =
  | { readonly kind: "written"; readonly path: string; readonly reason: "new" | "own" | "migrated" }
  | { readonly kind: "blocked"; readonly path: string };

// Write one rendered skill body to `absPath`, same clobber contract as apply-write.ts's
// writeArtifact PLUS the one-time migration carve-out: an existing file with no origin marker
// that is byte-for-byte equal to `legacyRendered` (what the pre-marker template produced) is a
// leftover from before this fix, not a foreign file — it is promoted in place (reason
// "migrated"). Anything else unmarked is a real user file and is refused, untouched, same as
// writeArtifact.
function writeSkillArtifact(absPath: string, rendered: string, legacyRendered: string): SkillWriteOutcome {
  if (existsSync(absPath)) {
    let existing: string | undefined;
    try {
      existing = readFileSync(absPath, "utf8");
    } catch {
      existing = undefined; // unreadable — let writeArtifact's own guard classify it (blocked)
    }
    if (existing !== undefined && !hasPetboxMarker(existing) && existing === legacyRendered) {
      mkdirSync(dirname(absPath), { recursive: true });
      writeFileSync(absPath, rendered, "utf8");
      return { kind: "written", path: absPath, reason: "migrated" };
    }
  }
  const outcome = writeArtifact(absPath, rendered);
  return outcome.kind === "blocked"
    ? { kind: "blocked", path: absPath }
    : { kind: "written", path: absPath, reason: outcome.reason };
}

// Render every PROJECT_SKILLS entry from templatesRoot and write it into every SKILL_SURFACES
// root under dir. Returns one outcome per (skill × surface), in write order, for the caller's
// log lines — a "blocked" outcome means a real, non-PetBox file already sat at that path and was
// left byte-for-byte untouched (see writeSkillArtifact above).
export function writeSkillFiles(
  dir: string,
  templatesRoot: string,
  project: string,
  workspace: string,
): SkillWriteOutcome[] {
  const outcomes: SkillWriteOutcome[] = [];
  for (const spec of PROJECT_SKILLS) {
    const tpl = readFileSync(join(templatesRoot, spec.dir, "SKILL.md"), "utf8");
    const rendered = renderSkillTemplate(tpl, project, workspace);
    const legacyRendered = stripMarkerLine(rendered);
    for (const surface of SKILL_SURFACES) {
      const skillPath = join(dir, ...surface, spec.dir, "SKILL.md");
      outcomes.push(writeSkillArtifact(skillPath, rendered, legacyRendered));
    }
  }
  return outcomes;
}
