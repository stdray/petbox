// The list of project-relative paths this kit generates — the one place that list exists
// (card: normalize-all-environments-to-default, items 3 and 5).
//
// Two different consumers need two different shapes of it, and conflating them was the trap:
//   - `.gitignore` needs PATTERNS. A skill folder is ours whole, so it goes in as a directory. A
//     harness agent directory is NOT ours whole — `~/.factory/droids` and `.claude/agents` are
//     places a person keeps their own files too — so role paths go in as the `petbox-*.md` glob
//     and nothing else. Ignoring a whole agent directory would hide a user's own agents from
//     their own `git status`, which is not the kit's call to make.
//   - the git-state report needs CONCRETE paths, because `git check-ignore` and `git ls-files`
//     answer about paths, not about globs. So role files are enumerated from disk instead.
//
// Plain TS for native node type-stripping: zero deps.

import { existsSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { agentFilesDir } from "./apply-artifacts.ts";
import { HARNESS_IDS } from "./harness-capabilities.ts";
import { readArtifactState } from "./origin-marker.ts";
import { PROJECT_SKILLS, SKILL_SURFACES } from "./skill-files.ts";

/** A generated role artifact's basename, in either harness spelling. Same shape apply-orphans.ts
 * trusts for deletion — kept identical on purpose, so "what we ignore" and "what we may delete"
 * describe the same set of files. */
const ROLE_FILE_RE = /^petbox-[a-z0-9_-]+\.md$/;

/** Skill directories, project-relative, POSIX-spelled: one per (skill x surface). */
export function managedSkillDirs(): string[] {
  const out: string[] = [];
  for (const surface of SKILL_SURFACES) {
    for (const spec of PROJECT_SKILLS) out.push([...surface, spec.dir].join("/"));
  }
  return out;
}

/** Agent directories, project-relative, POSIX-spelled: one per harness. */
export function managedAgentDirs(): string[] {
  return HARNESS_IDS.map((h) => agentFilesDir(h));
}

/**
 * The `.gitignore` block's entries. Skill folders whole; role artifacts by glob only (see header).
 * Stable, sorted, and independent of what happens to exist on disk — the block must not churn
 * from one apply to the next, or item 6's idempotency check would fail for a cosmetic reason.
 */
export function managedGitignoreEntries(): string[] {
  const skills = managedSkillDirs()
    .map((d) => `${d}/`)
    .sort();
  const roles = managedAgentDirs()
    .map((d) => `${d}/petbox-*.md`)
    .sort();
  return [...skills, ...roles];
}

/**
 * Every GENERATED role artifact that exists under `root` right now, project-relative and
 * POSIX-spelled. A project on the target default has NONE of these — the roles live once in the
 * harness profiles instead — so this is the measurement that says whether the migration landed.
 *
 * Marker-gated, and that is load-bearing rather than cautious. A file matching `petbox-*.md` that
 * carries no `petbox: managed` marker is somebody's OWN file that merely shares our namespace;
 * `apply` will never delete it (apply-orphans.ts), so counting it here would report a project as
 * permanently off-policy with no command that could ever fix it. What `status` calls a leftover
 * has to be exactly what `apply` would remove.
 *
 * Read-only and never throws: an unreadable directory is skipped, not an error.
 */
export function projectRoleFiles(root: string): string[] {
  const out: string[] = [];
  for (const dir of managedAgentDirs()) {
    const abs = join(root, dir);
    if (!existsSync(abs)) continue;
    let entries: string[];
    try {
      entries = readdirSync(abs).sort();
    } catch {
      continue;
    }
    for (const name of entries) {
      if (!ROLE_FILE_RE.test(name)) continue;
      if (readArtifactState(join(abs, name)) !== "ours") continue;
      out.push(`${dir}/${name}`);
    }
  }
  return out;
}

/**
 * Concrete project-relative paths for the git-state report: every managed skill directory, plus
 * every generated role file that actually exists under `root` right now. A project on the target
 * default has zero of the latter — which is exactly the fact the report needs to be able to state.
 */
export function managedPathsForGitState(root: string): string[] {
  return [...managedSkillDirs(), ...projectRoleFiles(root)];
}
