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
import { hasPetboxMarker, readPetboxProvenance } from "./origin-marker.ts";

export type WriteOutcome =
  | {
      readonly kind: "written";
      readonly path: string;
      readonly reason: "new" | "own" | "unchanged" | "adopted";
    }
  | { readonly kind: "blocked"; readonly path: string };

export type WriteArtifactOptions = {
  /** Compute and return the outcome WITHOUT touching the filesystem (task:
   * kit-version-lands-everywhere-and-sweeps item 2 — a registry-wide `apply --all` must be able
   * to preview outcomes across other people's project directories before writing anything).
   * Never set by any existing caller; defaults to false, so a bare `writeArtifact(path, content)`
   * behaves exactly as before, byte for byte. */
  readonly dryRun?: boolean;
  /**
   * This EXACT path was named on the command line with `--adopt` (card:
   * normalize-all-environments-to-default item 2). An unmarked file here is then treated as an
   * old PetBox render rather than a stranger's file and is overwritten (reason "adopted").
   *
   * Deliberately a per-path boolean the CALLER resolves, never a set this module searches and
   * never a global "force": the whole safety property of `--adopt` is that it changes the verdict
   * for the paths the operator typed and for nothing else. A `petbox: manual` declaration still
   * wins over it — that is the project asserting ownership, not an unmarked leftover.
   */
  readonly adopt?: boolean;
};

/**
 * Write one generated file to `absPath`.
 *  - Path does not exist → write it (reason "new").
 *  - Path exists, carries our origin marker, and the content DIFFERS → overwrite silently
 *    (reason "own") — this is the routine, expected re-apply case.
 *  - Path exists, is ours, and the content is already byte-identical → reason "unchanged", and
 *    nothing is written at all (dry run or not — see the comparison's own comment on why that
 *    symmetry is the point).
 *  - Path exists and does NOT carry our marker (a real file we did not create, or one we
 *    cannot even read) → refuse. Returns "blocked"; the file is left byte-for-byte untouched.
 * Never throws for the ordinary cases above (a directory-creation failure still throws — that
 * is a genuine environment error, not a clobber decision).
 *
 * `opts.dryRun: true` computes the SAME outcome (including the clobber-refusal check) but never
 * calls mkdirSync/writeFileSync — the one place that distinction is made, so a preview run and a
 * real run can never disagree about what would happen.
 */
export function writeArtifact(absPath: string, content: string, opts: WriteArtifactOptions = {}): WriteOutcome {
  const existed = existsSync(absPath);
  let adopted = false;
  if (existed) {
    let existing: string;
    try {
      existing = readFileSync(absPath, "utf8");
    } catch {
      // Unreadable existing entry (permissions, a directory, binary junk, ...) — treat as
      // foreign rather than guess; never overwrite something we could not even inspect. Not
      // adoptable either: `--adopt` says "this is an old render of ours", and a file we cannot
      // read is one we cannot say that about.
      return { kind: "blocked", path: absPath };
    }
    if (!hasPetboxMarker(existing)) {
      // `petbox: manual` outranks `--adopt`: that is the PROJECT declaring the path its own, a
      // live statement, not an unmarked leftover from before the marker existed.
      const declaredManual = readPetboxProvenance(existing) === "manual";
      if (!opts.adopt || declaredManual) return { kind: "blocked", path: absPath };
      adopted = true;
    }
    // Byte-identical → "unchanged", in a REAL run as well as a dry one, and no write at all.
    //
    // This used to be a dry-run-only comparison, and that made idempotence unobservable: a second
    // real `apply` re-wrote every file it had just written and reported each one as "wrote …
    // (updated in place — ours)". A re-run that says it wrote 15 files is indistinguishable, to a
    // reader and to a script, from one that actually changed something — so "run it twice and the
    // second run is a no-op" (card: normalize-all-environments-to-default item 6) could not be
    // checked at all. Comparing here also stops touching mtimes on files nothing changed.
    if (existing === content) return { kind: "written", path: absPath, reason: "unchanged" };
    if (opts.dryRun) {
      return { kind: "written", path: absPath, reason: adopted ? "adopted" : "own" };
    }
  } else if (opts.dryRun) {
    return { kind: "written", path: absPath, reason: "new" };
  }
  mkdirSync(dirname(absPath), { recursive: true });
  writeFileSync(absPath, content, "utf8");
  return {
    kind: "written",
    path: absPath,
    reason: existed ? (adopted ? "adopted" : "own") : "new",
  };
}

export type LegacyCleanupOutcome = "removed" | "kept-foreign" | "absent";

/**
 * Delete the file at `absPath` IF AND ONLY IF it carries our origin marker. The single
 * deletion primitive of this package: "absent" when there is nothing there, "removed" when an
 * owned file was deleted, "kept-foreign" when something exists there that is NOT ours — left
 * untouched, never deleted, never renamed. Unreadable counts as foreign, exactly like
 * writeArtifact: never destroy what we could not even inspect.
 *
 * Two callers with two DIFFERENT reasons, and they must not be conflated
 * (bug: artifact-integrity-dangling-and-orphans):
 *   - cleanupLegacyArtifact — a RENAME leftover (`worker.md` superseded by `petbox-worker.md`).
 *     The role still exists; only its filename moved. Runs per written file, right after the
 *     replacement landed.
 *   - apply-orphans.ts's sweepOrphanArtifacts — a role that is GONE from the definition. No
 *     replacement exists or ever will; the file is a standing instruction to use a role that
 *     no longer exists. Runs as its own pass over the harness's agent directory.
 *
 * `dryRun: true` returns the same verdict ("removed" means "would be removed") without calling
 * unlinkSync — same preview contract as writeArtifact's `opts.dryRun` above.
 */
export function removeOwnedArtifact(absPath: string, opts: WriteArtifactOptions = {}): LegacyCleanupOutcome {
  if (!existsSync(absPath)) return "absent";
  let existing: string;
  try {
    existing = readFileSync(absPath, "utf8");
  } catch {
    return "kept-foreign";
  }
  if (!hasPetboxMarker(existing)) return "kept-foreign";
  if (!opts.dryRun) unlinkSync(absPath);
  return "removed";
}

/**
 * Remove an old, pre-namespacing artifact at `absPath` IF AND ONLY IF it carries our origin
 * marker. Returns "absent" when there is nothing there (the common steady-state case once
 * migration has run once), "removed" when an owned leftover was deleted, "kept-foreign" when
 * something exists there that is NOT ours — left untouched, never deleted, never renamed.
 */
export function cleanupLegacyArtifact(absPath: string, opts: WriteArtifactOptions = {}): LegacyCleanupOutcome {
  return removeOwnedArtifact(absPath, opts);
}
