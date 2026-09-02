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
export const PETBOX_MANUAL_VALUE = "manual";
/** The literal frontmatter line every renderer appends to a generated file. */
export const PETBOX_MARKER_LINE = `${PETBOX_MARKER_KEY}: ${PETBOX_MARKER_VALUE}`;
/** The literal frontmatter line a project uses to declare a path as ITS OWN, hands off. */
export const PETBOX_MANUAL_LINE = `${PETBOX_MARKER_KEY}: ${PETBOX_MANUAL_VALUE}`;

// Invocation-mode declaration, a SEPARATE frontmatter key from the provenance one above
// (spec: wire-skill-invocation-mode). Provenance answers "may the kit write/delete this path";
// mode answers "should an agent be told about this skill without being asked". They are
// independent axes — a kit-managed skill can be manual-invocation (`petbox-factory-run`), and a
// project's own manual-provenance skill is simply never in the kit's digest at all.
export const PETBOX_DIGEST_KEY = "petbox-digest";
export type SkillDigestMode = "auto" | "manual";

/**
 * The three ORIGIN states of a file on a path the kit may want to write
 * (spec: wire-skill-provenance-states):
 *   - "managed" — the kit renders it and is its only source of truth; safe to overwrite AND the
 *     only state in which anything may ever be deleted;
 *   - "manual"  — the PROJECT declared this path its own; the kit must leave it alone, and that
 *     is a legal, non-error outcome (spec: wire-skill-manual-declared-not-error);
 *   - null      — undeclared: a foreign file. Same refusal as before, exit 1 at the call site.
 * Deliberately a declaration, never a name/prefix heuristic: every delivered skill is heading
 * for a `petbox-*` directory name (work: petbox-skill-naming), after which a name tells you
 * nothing about who owns the file. `petbox-methodology-system` is the live proof — `petbox-`
 * prefixed, repo-native, never delivered by the kit.
 */
export type PetboxProvenance = "managed" | "manual";

const FRONTMATTER_RE = /^---\r?\n([\s\S]*?)\r?\n---/;

/** The raw YAML frontmatter block, or null when `content` has none. */
function frontmatterOf(content: string): string | null {
  const m = content.match(FRONTMATTER_RE);
  // The capture group is mandatory in the pattern above (no `?`), so a successful match
  // always populates it — but a stray content string could still fail to match at all.
  return m?.[1] ?? null;
}

/** The single-token value of frontmatter key `key`, or null. `petbox:` never matches
 * `petbox-digest:` — the colon is part of the pattern. */
function frontmatterValue(content: string, key: string): string | null {
  const frontmatter = frontmatterOf(content);
  if (frontmatter === null) return null;
  const m = frontmatter.match(new RegExp(`^${key}:[ \\t]*(\\S+)[ \\t]*\\r?$`, "m"));
  return m?.[1] ?? null;
}

/**
 * Which of the three provenance states `content` declares, read from its YAML frontmatter (the
 * block between the first pair of `---` lines). Frontmatter-scoped on purpose: a user's OWN file
 * that happens to mention the word "petbox" in its BODY prose must never be mistaken for ours. A
 * file with no frontmatter at all (no leading `---` block) is undeclared — it cannot be one of
 * our renders. An UNRECOGNIZED value (`petbox: something-else`) is undeclared too: the guards
 * below only ever act on a state they actually understand.
 */
export function readPetboxProvenance(content: string): PetboxProvenance | null {
  const value = frontmatterValue(content, PETBOX_MARKER_KEY);
  if (value === PETBOX_MARKER_VALUE) return "managed";
  if (value === PETBOX_MANUAL_VALUE) return "manual";
  return null;
}

/**
 * True ONLY for `petbox: managed`. This is the write/delete gate of the whole package
 * (apply-write.ts) and it is deliberately narrow: before the provenance states existed it
 * accepted ANY `petbox: <token>` value, which would have made a file declared `petbox: manual`
 * silently overwritable and — worse — deletable by cleanupLegacyArtifact.
 */
export function hasPetboxMarker(content: string): boolean {
  return readPetboxProvenance(content) === "managed";
}

/** True for `petbox: manual` — the project declared this path its own. Never written, never
 * deleted, and never counted as a conflict (spec: wire-skill-manual-declared-not-error). */
export function isDeclaredManual(content: string): boolean {
  return readPetboxProvenance(content) === "manual";
}

/**
 * The invocation mode `content` declares (`petbox-digest: auto|manual`), or null when it
 * declares none. Only "auto" ever puts a skill into the agent's automatic digest — an
 * undeclared file is out, which is what keeps a project's own skills (and any future
 * `petbox-*`-renamed manual one) from leaking into it by name alone.
 */
export function readDigestMode(content: string): SkillDigestMode | null {
  const value = frontmatterValue(content, PETBOX_DIGEST_KEY);
  return value === "auto" || value === "manual" ? value : null;
}

// Materialization fact for one path apply/wire may have written: absent (never written), ours
// (`petbox: managed` — safe to overwrite silently), manual (`petbox: manual` — the project
// declared the path its own: left alone, and NOT a defect to report), or foreign (undeclared —
// something else's file sits there: refuse, never touch it). One state per provenance value, so
// every reader (doctor's blocked/drifted counts, status's per-file lines) can tell "left alone
// on purpose" from "left alone because it is a problem". Shared by every caller that needs to
// know "is this ours" before comparing content — status's per-role/per-skill lines and
// skill-files.ts's checkSkillFile both classify with this SAME function, never a second content
// heuristic (moved here from status.ts: not skill- or role-specific, it belongs next to the
// marker it reads — see this file's header).
export type ArtifactState = "absent" | "ours" | "manual" | "foreign";

export function readArtifactState(absPath: string): ArtifactState {
  if (!existsSync(absPath)) return "absent";
  let content: string;
  try {
    content = readFileSync(absPath, "utf8");
  } catch {
    return "foreign"; // unreadable — apply-write.ts treats this the same way (refuse, not ours)
  }
  const provenance = readPetboxProvenance(content);
  if (provenance === "managed") return "ours";
  if (provenance === "manual") return "manual";
  return "foreign";
}
