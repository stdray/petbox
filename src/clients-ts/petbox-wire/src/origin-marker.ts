// The origin marker every petbox-wire `apply` render embeds into a generated file's YAML
// frontmatter, and the ONLY thing the write guard (apply-write.ts) trusts to recognize "this
// file is ours" before it ever overwrites or deletes something already on disk — no content
// heuristics, no filename convention, no timestamp guess.
//
// Why this exists (bug: apply-clobbers-user-agent-files): `apply` used to writeFileSync
// unconditionally. A user with their OWN `.claude/agents/worker.md` lost it silently on the
// first apply — the only trace that a file was "ours" was a `description: PetBox <tier> role
// (<slug>)` line INSIDE the file apply had already overwritten, which is useless after the
// fact. The marker line below is written BEFORE any write decision is made, so a pre-existing
// file can be classified accurately: ours (marker present → safe to update silently) or a
// real user file (marker absent → refuse, loudly, never touch it).
//
// Plain TS.

import { existsSync, readFileSync } from "node:fs";

export const PETBOX_MARKER_KEY = "petbox";
export const PETBOX_MARKER_VALUE = "managed";
/** The literal frontmatter line every renderer appends to a generated file. */
export const PETBOX_MARKER_LINE = `${PETBOX_MARKER_KEY}: ${PETBOX_MARKER_VALUE}`;

const MARKER_LINE_RE = new RegExp(`^${PETBOX_MARKER_KEY}:\\s*\\S+`, "m");

/**
 * True when `content`'s YAML frontmatter (the block between the first pair of `---` lines)
 * carries our origin marker. Frontmatter-scoped on purpose: a user's OWN file that happens to
 * mention the word "petbox" in its BODY prose must never be mistaken for ours. A file with no
 * frontmatter at all (no leading `---` block) never matches — it cannot be one of our renders.
 */
export function hasPetboxMarker(content: string): boolean {
  const m = content.match(/^---\r?\n([\s\S]*?)\r?\n---/);
  if (!m) return false;
  const frontmatter = m[1];
  // The capture group is mandatory in the pattern above (no `?`), so a successful match
  // always populates it — but a stray content string could still fail to match at all.
  if (frontmatter === undefined) return false;
  return MARKER_LINE_RE.test(frontmatter);
}

// Materialization fact for one path apply/wire may have written: absent (never written), ours
// (carries the marker — safe to overwrite silently), or foreign (something else's file sits
// there — refuse, never touch it). Shared by every caller that needs to know "is this ours"
// before comparing content — status's per-role/per-skill lines and skill-files.ts's
// checkSkillFile both classify with this SAME function, never a second content heuristic
// (moved here from status.ts: not skill- or role-specific, it belongs next to the marker it
// reads — see this file's header).
export type ArtifactState = "absent" | "ours" | "foreign";

export function readArtifactState(absPath: string): ArtifactState {
  if (!existsSync(absPath)) return "absent";
  let content: string;
  try {
    content = readFileSync(absPath, "utf8");
  } catch {
    return "foreign"; // unreadable — apply-write.ts treats this the same way (refuse, not ours)
  }
  return hasPetboxMarker(content) ? "ours" : "foreign";
}
