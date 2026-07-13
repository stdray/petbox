// Filesystem writer for petbox-wire apply's PlannedFiles.
//
// Kept out of apply-artifacts.ts (which stays pure / filesystem-free by its own contract —
// "Does not touch the filesystem") and out of wire.ts (whose main() runs at import time, so
// helpers that need to be unit-testable in isolation live in a side module — same pattern as
// posix-env.ts).
//
// The whole point (bug: apply-clobbers-user-agent-files): before this module existed, apply's
// write loop was an unconditional writeFileSync — no existence check, no origin marker, no
// refusal. A user's own `.claude/agents/worker.md` was destroyed on the first `apply` with zero
// warning. writeArtifact fixes that with ONE rule: an existing file is only ever overwritten
// when it already carries OUR origin marker (origin-marker.ts); anything else is refused, loud,
// non-zero exit at the call site — never partially touched.
//
// cleanupLegacyArtifact is the companion used by the namespaced-agent-names rename
// (petbox-namespaced-agent-names): once role files are emitted under a new `petbox-<slug>`
// name, the OLD unprefixed file (e.g. `.claude/agents/worker.md`) would otherwise be left
// behind as an orphan. It is only ever removed when it carries our marker — a real user file
// that happens to share the old bare name is left alone, exactly like writeArtifact.
//
// Plain TS for native node type-stripping: zero deps.

import { existsSync, mkdirSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { hasPetboxMarker } from "./origin-marker.ts";

export type WriteOutcome =
  | { readonly kind: "written"; readonly path: string; readonly reason: "new" | "own" }
  | { readonly kind: "blocked"; readonly path: string };

/**
 * Write one generated file to `absPath`.
 *  - Path does not exist → write it (reason "new").
 *  - Path exists and its frontmatter carries our origin marker → overwrite silently
 *    (reason "own") — this is the routine, expected re-apply case.
 *  - Path exists and does NOT carry our marker (a real file we did not create, or one we
 *    cannot even read) → refuse. Returns "blocked"; the file is left byte-for-byte untouched.
 * Never throws for the ordinary cases above (a directory-creation failure still throws — that
 * is a genuine environment error, not a clobber decision).
 */
export function writeArtifact(absPath: string, content: string): WriteOutcome {
  const existed = existsSync(absPath);
  if (existed) {
    let existing: string;
    try {
      existing = readFileSync(absPath, "utf8");
    } catch {
      // Unreadable existing entry (permissions, a directory, binary junk, ...) — treat as
      // foreign rather than guess; never overwrite something we could not even inspect.
      return { kind: "blocked", path: absPath };
    }
    if (!hasPetboxMarker(existing)) {
      return { kind: "blocked", path: absPath };
    }
  }
  mkdirSync(dirname(absPath), { recursive: true });
  writeFileSync(absPath, content, "utf8");
  return {
    kind: "written",
    path: absPath,
    reason: existed ? "own" : "new",
  };
}

export type LegacyCleanupOutcome = "removed" | "kept-foreign" | "absent";

/**
 * Remove an old, pre-namespacing artifact at `absPath` IF AND ONLY IF it carries our origin
 * marker. Returns "absent" when there is nothing there (the common steady-state case once
 * migration has run once), "removed" when an owned leftover was deleted, "kept-foreign" when
 * something exists there that is NOT ours — left untouched, never deleted, never renamed.
 */
export function cleanupLegacyArtifact(absPath: string): LegacyCleanupOutcome {
  if (!existsSync(absPath)) return "absent";
  let existing: string;
  try {
    existing = readFileSync(absPath, "utf8");
  } catch {
    return "kept-foreign";
  }
  if (!hasPetboxMarker(existing)) return "kept-foreign";
  unlinkSync(absPath);
  return "removed";
}
