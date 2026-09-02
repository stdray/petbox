// Orphan sweep: delete the artifact of a role that is GONE from the definition.
//
// Why this exists (bug: artifact-integrity-dangling-and-orphans, decision D11). apply had two
// cleanup passes and NEITHER was about a vanished role:
//   - wire.ts's kit-mirror orphan cleanup — files this kit no longer ships, under ~/.petbox/wire;
//   - apply-write.ts's cleanupLegacyArtifact — a RENAME leftover (`worker.md` after
//     `petbox-worker.md` landed). The role still exists; only its filename moved.
// So removing a role from the definition was PHYSICALLY IMPOSSIBLE: its `petbox-<slug>.md`
// stayed registered in the harness forever, telling agents to use a role nothing defines. That
// was harmless only while the roster never shrank — which is exactly what layer subtraction
// (definition-layer-cascade) is about to change, hence this being a PRECONDITION of the
// resolver rather than a neighbouring cleanup.
//
// The rule, and it is the whole module: a candidate is deleted ONLY when it carries the PetBox
// origin marker (origin-marker.ts, via apply-write.ts's removeOwnedArtifact). A user's own
// `.claude/agents/petbox-something.md` — someone else's file that merely happens to sit in our
// namespace — is reported and left byte-for-byte alone. The marker is the only signal trusted
// for deletion anywhere in this package; there is no filename heuristic and no timestamp guess.
//
// Covers all three harness layouts (.claude/agents, .opencode/agent, .factory/droids) because
// it asks apply-artifacts.ts for both the directory AND the expected basenames — droid's
// name sanitization included. Nothing here re-derives a path or a filename.
//
// Plain TS for native node type-stripping: zero deps.

import { existsSync, readdirSync, rmdirSync, statSync } from "node:fs";
import { join } from "node:path";
import type { AgentDefinition } from "./agent-definition.ts";
import { agentFilesDir, expectedArtifactBasenames } from "./apply-artifacts.ts";
import { removeOwnedArtifact } from "./apply-write.ts";
import type { HarnessId } from "./harness-capabilities.ts";

/**
 * Only files in OUR namespace are ever candidates. A bare `worker.md` (the pre-namespacing
 * name) deliberately does NOT match: that is the legacy-rename path's business
 * (cleanupLegacyArtifact), which runs only after a successful replacement write. Conflating
 * the two would let a definition-resolution hiccup delete a user's freshly renamed file.
 */
const OURS_RE = /^petbox-[a-z0-9_-]+\.md$/;

export type OrphanOutcome = {
  readonly path: string;
  /** "removed" — ours and gone; "kept-foreign" — no origin marker, left untouched. */
  readonly outcome: "removed" | "kept-foreign";
};

/**
 * Remove every `petbox-*.md` in `harness`'s agent directory under `root` whose role is not in
 * `definition`. Returns one entry per file acted on or refused; an untouched, still-declared
 * role produces nothing. Never throws for the ordinary cases: a missing directory, an
 * unreadable entry and a path that turns out to be a directory are all simply skipped.
 */
export function sweepOrphanArtifacts(
  root: string,
  harness: HarnessId,
  definition: AgentDefinition,
  opts: { readonly dryRun?: boolean } = {},
): OrphanOutcome[] {
  return sweepOrphanArtifactsIn(join(root, agentFilesDir(harness)), harness, definition, opts);
}

/**
 * The same sweep against an ARBITRARY agent directory. Exists because role artifacts no longer
 * live only in project trees: with `--roles=user` the authoritative copy sits in the harness's own
 * profile (`~/.claude/agents`, `~/.config/opencode/agents`, `~/.factory/droids` — role-scope.ts),
 * and a role dropped from the definition has to lose its file THERE too. Same marker gate, same
 * namespace gate; only the directory is a parameter.
 */
export function sweepOrphanArtifactsIn(
  dir: string,
  harness: HarnessId,
  definition: AgentDefinition,
  opts: { readonly dryRun?: boolean } = {},
): OrphanOutcome[] {
  if (!existsSync(dir)) return [];
  let entries: string[];
  try {
    entries = readdirSync(dir).sort();
  } catch {
    return [];
  }

  const expected = expectedArtifactBasenames(definition, harness);
  const outcomes: OrphanOutcome[] = [];
  for (const name of entries) {
    if (!OURS_RE.test(name)) continue;
    if (expected.has(name)) continue;
    const abs = join(dir, name);
    try {
      if (!statSync(abs).isFile()) continue;
    } catch {
      continue;
    }
    const outcome = removeOwnedArtifact(abs, opts);
    if (outcome === "absent") continue; // raced away between readdir and unlink
    outcomes.push({ path: abs, outcome });
  }
  return outcomes;
}

/**
 * Remove EVERY generated role artifact in `harness`'s PROJECT agent directory under `root` — not
 * just the orphans (card: normalize-all-environments-to-default item 1).
 *
 * This is the migration to user-scope roles: once the same five roles are rendered once into the
 * harness's own profile, the per-project copies are duplicates that can only drift, and 90 of them
 * across eight projects already had. Unlike sweepOrphanArtifacts this does NOT consult the
 * definition — a role that is still perfectly current is exactly what is being removed here,
 * because its authoritative copy now lives somewhere else.
 *
 * Everything else is identical and non-negotiable: a candidate must match our namespace AND carry
 * the `petbox: managed` marker, or it is reported and left byte-for-byte alone. `pruneEmptyDir`
 * additionally removes the now-empty directory itself — used for opencode's legacy singular
 * `.opencode/agent`, which the target layout drops entirely; `rmdirSync` throws ENOTEMPTY when
 * anything else lives there, so a directory holding someone's own files always survives.
 */
export function sweepProjectRoleArtifacts(
  root: string,
  harness: HarnessId,
  opts: { readonly dryRun?: boolean; readonly pruneEmptyDir?: boolean } = {},
): OrphanOutcome[] {
  const dir = join(root, agentFilesDir(harness));
  if (!existsSync(dir)) return [];
  let entries: string[];
  try {
    entries = readdirSync(dir).sort();
  } catch {
    return [];
  }

  const outcomes: OrphanOutcome[] = [];
  for (const name of entries) {
    if (!OURS_RE.test(name)) continue;
    const abs = join(dir, name);
    try {
      if (!statSync(abs).isFile()) continue;
    } catch {
      continue;
    }
    const outcome = removeOwnedArtifact(abs, { ...(opts.dryRun !== undefined ? { dryRun: opts.dryRun } : {}) });
    if (outcome === "absent") continue;
    outcomes.push({ path: abs, outcome });
  }

  if (opts.pruneEmptyDir && !opts.dryRun && outcomes.every((o) => o.outcome !== "kept-foreign")) {
    try {
      rmdirSync(dir);
    } catch {
      // not empty (someone else's files live here) or already gone — either way, leave it
    }
  }
  return outcomes;
}
