// `--adopt <absolute path>` — the ONE narrow way a file that does not carry the PetBox origin
// marker may still be overwritten (card: normalize-all-environments-to-default, item 2).
//
// Why it is shaped like this, and why there is deliberately no `--force`. Three registry projects
// are blocked today by a `petbox/SKILL.md` that WE rendered, before the marker existed — apply
// cannot tell it from a stranger's file, so it refuses, correctly. The fix a `--force` flag would
// offer is a bulk "overwrite everything you were refused", which across seven OTHER people's
// working directories is exactly the destructive mode the card forbids. So the only lever is a
// per-path one: the operator reads the refusal, names that exact path, and nothing else changes
// behavior. A path that was never refused is simply never used; a refusal on a path nobody named
// stays a refusal, and the run still exits 1.
//
// Path comparison is normalized because the two sides genuinely differ in spelling: apply builds
// its targets from `git rev-parse --show-toplevel`, which on Windows returns forward slashes
// (`D:/my/prj/petbox`), while the operator pastes the backslash form the refusal printed. Case is
// folded on win32 only — on POSIX two paths differing in case are two different files.
//
// Plain TS for native node type-stripping: zero deps.

import { resolve } from "node:path";

/** Normalized comparison key for an absolute path (see the header on why this is not identity). */
export function pathKey(p: string, platform: string = process.platform): string {
  const abs = resolve(p).replace(/\\/g, "/").replace(/\/+$/, "");
  return platform === "win32" ? abs.toLowerCase() : abs;
}

export type AdoptSet = {
  /** True when this path was named on the command line — the caller may then overwrite it. */
  readonly has: (absPath: string) => boolean;
  /** Record that apply CONSIDERED this path, whatever the outcome. Marks a named path as used. */
  readonly consider: (absPath: string) => void;
  /** Named paths apply never even looked at — a typo, or a path from another project. */
  readonly unmatched: () => readonly string[];
  readonly size: number;
};

/**
 * Build the set from the raw `--adopt` values. `consider` (not `has`) is what marks a path as
 * matched: a second run of the same command finds the file ALREADY adopted — marker present, no
 * refusal, `has` never consulted — and reporting that as "you named a path I never saw" would
 * make the verb non-idempotent for no reason.
 */
export function createAdoptSet(paths: readonly string[], platform: string = process.platform): AdoptSet {
  const keys = new Map<string, string>(); // normalized key -> as the operator typed it
  for (const p of paths) keys.set(pathKey(p, platform), p);
  const seen = new Set<string>();
  return {
    has: (absPath: string) => keys.has(pathKey(absPath, platform)),
    consider: (absPath: string) => {
      const k = pathKey(absPath, platform);
      if (keys.has(k)) seen.add(k);
    },
    unmatched: () => [...keys.entries()].filter(([k]) => !seen.has(k)).map(([, raw]) => raw),
    size: keys.size,
  };
}

/** The empty set — every call site that has no `--adopt` uses this, never `undefined` checks. */
export const NO_ADOPT: AdoptSet = createAdoptSet([]);
