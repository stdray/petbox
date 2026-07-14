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
// Plain TS for native node type-stripping: zero deps.

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
