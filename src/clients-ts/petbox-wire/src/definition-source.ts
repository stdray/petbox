// WHERE the agent definition comes from — the ONE answer, for every caller in this kit.
//
// Specs: definition-layer-cascade (the cascade itself), broken-layer-fails-loudly (what a broken
// layer does). Decisions D13/D18, card wire-stops-fetching-definition (stage 2).
//
// The definition is BUILT FROM FILES, in a declared order:
//
//   base      the document shipped inside this package (agent-definition.ts's
//             DEFAULT_AGENT_DEFINITION / default-agents.json). Always present — its absence
//             throws at import, before anything here runs.
//   user      ~/.petbox/agents            — a machine-wide layer
//   project   <apply root>/.petbox/agents — a per-worktree layer
//
// Nothing here talks to a server, and no caller in this kit may resolve a definition any other
// way. That is the whole point of stage 2: the kit stopped asking PetBox what the roles are.
// Previously the first thing EVERY session start did on all three harnesses was an HTTP fetch,
// with a last-known-good replica on disk behind it — which is exactly the shape that turns a
// broken source into a silent one (the replica keeps answering for days). There is deliberately
// no cache here, and there is nothing to cache: the layers ARE files, already local.
//
// TWO MODES, and the difference is the point of the broken-layer spec:
//
//   BUILD  (apply, doctor)   — resolveLocalDefinition. A present-but-broken layer THROWS
//                              (LayerSourceError) or comes back with cascade errors; the caller
//                              refuses, names the absolute path, and writes NOTHING. Artifacts
//                              that were built from a source nobody could read are worse than
//                              artifacts that were not built at all.
//   RENDER (SessionStart)    — resolveDefinitionForSession. NEVER throws, never exits non-zero:
//                              a SessionStart hook that crashes is worse than one that degrades
//                              (wire-log.ts's Class-Б taxonomy). A broken layer instead produces
//                              a MARKER LINE that leads the banner, a wire.log trace, and a
//                              protocol rendered from the base. The loudness lives in stdout,
//                              not in the exit code.
//
// Absence is not breakage. A layer directory that does not exist — or exists but DECLARES NOTHING
// (empty, or holding only `.DS_Store` / `Thumbs.db` / a README) — is a layer with NO OPINION
// (layer-cascade.ts's isLayerDirectory) and is the overwhelmingly common case, never worth a word
// of warning. Only a directory that states an intent (a `layer.json`, or a `petbox-*` document)
// and then cannot be read/parsed/validated is an error.
//
// Plain TS for native node type-stripping: zero deps.

import { homedir } from "node:os";
import { join } from "node:path";
import {
  DEFAULT_AGENT_DEFINITION,
  DEFAULT_AGENT_DEFINITION_PATH,
  KIT_VERSION,
  type AgentDefinition,
} from "./agent-definition.ts";
import {
  cascadeErrors,
  formatCascadeProvenance,
  isLayerDirectory,
  LayerSourceError,
  resolveDefinitionLayers,
  type BaseLayer,
  type CascadeDiagnostic,
  type CascadeResolution,
} from "./layer-cascade.ts";
import { wireLog } from "./wire-log.ts";

/** Provenance name of the shipped floor. Short on purpose — it is printed once per field. */
export const BASE_LAYER_NAME = "base";

/** Every optional layer lives at `<root>/.petbox/agents`; only the root differs. */
export const LAYER_DIR_SEGMENTS = [".petbox", "agents"] as const;

export type DefinitionLayerLabel = "user" | "project";

export type DefinitionLayerCandidate = {
  readonly label: DefinitionLayerLabel;
  readonly dir: string;
  /**
   * Does this directory DECLARE a layer (a `layer.json` or a `petbox-*` document)? Not merely
   * "does a directory exist there": an empty directory, or one holding only desktop/git service
   * files, states nothing and is treated exactly like one that was never created. See
   * layer-cascade.ts's isLayerDirectory for why that distinction is load-bearing rather than
   * lenient.
   */
  readonly present: boolean;
};

/** The kit's shipped floor, as a cascade layer. */
export function baseLayer(): BaseLayer {
  return {
    name: BASE_LAYER_NAME,
    dir: DEFAULT_AGENT_DEFINITION_PATH,
    definition: DEFAULT_AGENT_DEFINITION,
  };
}

export function userLayerDir(homeDir: string = homedir()): string {
  return join(homeDir, ...LAYER_DIR_SEGMENTS);
}

export function projectLayerDir(root: string): string {
  return join(root, ...LAYER_DIR_SEGMENTS);
}

/**
 * The canonical optional layers, lowest priority first. This list is the kit's ANSWER to "where
 * do layers live" — it used to be the `layers` diagnostic command's private, documented guess
 * ("this command's own choice"), which was honest only while nothing resolved definitions from
 * files. Now everything does, so there is exactly one list and it lives here.
 */
export function definitionLayerCandidates(
  root: string,
  homeDir: string = homedir(),
): DefinitionLayerCandidate[] {
  return [
    { label: "user" as const, dir: userLayerDir(homeDir) },
    { label: "project" as const, dir: projectLayerDir(root) },
  ].map((c) => ({ ...c, present: isLayerDirectory(c.dir) }));
}

export type LocalDefinition = {
  readonly definition: AgentDefinition;
  readonly resolution: CascadeResolution;
  /** Every candidate, present or not — so a caller can print WHAT IT LOOKED AT, not only what it found. */
  readonly candidates: ReadonlyArray<DefinitionLayerCandidate>;
  /** Cascade diagnostics of severity "error". Non-empty = a BUILD caller must refuse. */
  readonly errors: ReadonlyArray<CascadeDiagnostic>;
};

/**
 * BUILD path. Resolve base < user < project for `root`.
 *
 * Throws LayerSourceError when a PRESENT layer's source is unreadable — the message already
 * carries the absolute path and the parser's own position, and `.path` carries the path alone.
 * A resolution that succeeds but reports cascade ERRORS comes back with them in `errors`; that
 * is not a throw because the whole report is worth printing at once, but a build caller must
 * treat a non-empty `errors` exactly as it treats the throw: refuse, write nothing.
 */
export function resolveLocalDefinition(opts: {
  readonly root: string;
  readonly homeDir?: string;
}): LocalDefinition {
  const candidates = definitionLayerCandidates(opts.root, opts.homeDir ?? homedir());
  return resolveFromCandidates(candidates);
}

/**
 * BUILD path for USER-SCOPE role artifacts (`apply --roles=user`), which are a MACHINE fact:
 * base < user, with the PROJECT layer deliberately left out.
 *
 * Card user-scope-roles-rendered-from-cwd-project-definition measured what happens when a
 * machine-wide render takes a per-directory source: the same `apply --all --dry-run` rendered
 * different bytes into the same 15 paths depending on which project's turn it was, last write
 * winning, silently. Both layers here are machine-wide, so the render is identical from any cwd
 * — the property that card bought, kept, and now extended one layer up instead of frozen at the
 * kit floor.
 */
export function resolveUserScopeDefinition(opts: { readonly homeDir?: string } = {}): LocalDefinition {
  const homeDir = opts.homeDir ?? homedir();
  const dir = userLayerDir(homeDir);
  return resolveFromCandidates([{ label: "user", dir, present: isLayerDirectory(dir) }]);
}

function resolveFromCandidates(candidates: ReadonlyArray<DefinitionLayerCandidate>): LocalDefinition {
  const resolution = resolveDefinitionLayers(
    candidates.filter((c) => c.present).map((c) => c.dir),
    { base: baseLayer() },
  );
  return {
    definition: resolution.definition,
    resolution,
    candidates,
    errors: cascadeErrors(resolution),
  };
}

// ---- provenance rendering ---------------------------------------------------------------
//
// D18 makes these lines load-bearing rather than cosmetic. Stage 2's confirmation is "apply and
// SessionStart ran WITHOUT going to the server for the definition", and the only thing that can
// prove it is the run naming its own layers and, per field, which one supplied it.

/** One grep-able line: which layers this resolve is made of, in order, with their paths. */
export function formatDefinitionLayersLine(local: LocalDefinition): string {
  const layers = local.resolution.layers
    .map((l) =>
      l.mode === "base"
        ? `${l.name}[kit v${KIT_VERSION}] ${l.dir}`
        : `${l.name}[${l.mode}] ${l.dir}`,
    )
    .join("  <  ");
  const absent = local.candidates.filter((c) => !c.present);
  const absentNote =
    absent.length > 0
      ? ` (absent, no opinion: ${absent.map((c) => `${c.label}=${c.dir}`).join(", ")})`
      : "";
  return `layers=${local.resolution.layers.length}: ${layers}${absentNote}`;
}

/** Per-role, per-field origin — `formatCascadeProvenance`, verbatim; no second renderer. */
export function formatDefinitionProvenance(local: LocalDefinition): string {
  return local.definition.roles.length > 0
    ? formatCascadeProvenance(local.resolution)
    : "  (no roles resolved)";
}

/** `  <code> [<layer>] <message>` per cascade error — the refusal body for a BUILD caller. */
export function formatDefinitionErrors(errors: ReadonlyArray<CascadeDiagnostic>): string {
  return errors.map((d) => `  ${d.code}${d.layer ? ` [${d.layer}]` : ""} ${d.message}`).join("\n");
}

// ---- render path (SessionStart) ----------------------------------------------------------

export type SessionDefinition = {
  readonly definition: AgentDefinition;
  /**
   * "" when the cascade resolved cleanly. Otherwise a one-line marker naming the BROKEN FILE by
   * absolute path — prepended to the banner, ahead of the byte budget, exactly like
   * worktree-base-guard.ts's stale-base warning: tiny, highest priority, must survive any tail
   * truncation of the rest of the banner.
   */
  readonly note: string;
  /** True when the note is present and the protocol below it was rendered from the base alone. */
  readonly degraded: boolean;
};

/**
 * RENDER path. Never throws.
 *
 * A broken layer does NOT stop a session — "a SessionStart that crashes is worse than one that
 * stays quiet" is the invariant every injector is built on. It does, however, stop being quiet:
 * the banner LEADS with the path of the file that broke, `~/.petbox/wire.log` gets a Class-Б
 * trace, and the protocol underneath is rendered from the kit base rather than from a
 * half-applied cascade. There is no last-known-good substitution anywhere in this path — the
 * base is a shipped FLOOR, not a previously-successful result (D15).
 */
export function resolveDefinitionForSession(opts: {
  readonly root: string;
  readonly homeDir?: string;
  readonly logSource?: string;
}): SessionDefinition {
  const logSource = opts.logSource ?? "session";
  try {
    const local = resolveLocalDefinition({
      root: opts.root,
      ...(opts.homeDir !== undefined ? { homeDir: opts.homeDir } : {}),
    });
    if (local.errors.length > 0) {
      const first = local.errors[0] as CascadeDiagnostic;
      const detail = `${first.code}${first.layer ? ` [${first.layer}]` : ""} ${first.message}`;
      wireLog(
        logSource,
        `definition cascade reported ${local.errors.length} error(s); rendering the protocol from ` +
          `the kit base (${DEFAULT_AGENT_DEFINITION_PATH}). First: ${detail}`,
        opts.homeDir,
      );
      return {
        definition: DEFAULT_AGENT_DEFINITION,
        note:
          `⚠ definition layers are BROKEN — ${local.errors.length} cascade error(s); this session's ` +
          `protocol is rendered from the kit base (${DEFAULT_AGENT_DEFINITION_PATH}). First: ${detail}. ` +
          `Run \`petbox-wire layers\` to see the whole report.`,
        degraded: true,
      };
    }
    return { definition: local.definition, note: "", degraded: false };
  } catch (e) {
    const path = e instanceof LayerSourceError ? e.path : "";
    const message = e instanceof Error ? e.message : String(e);
    wireLog(
      logSource,
      `definition layer unreadable${path ? ` (${path})` : ""}: ${message} — rendering the protocol ` +
        `from the kit base (${DEFAULT_AGENT_DEFINITION_PATH})`,
      opts.homeDir,
    );
    return {
      definition: DEFAULT_AGENT_DEFINITION,
      note:
        `⚠ definition layer is BROKEN and was not read${path ? `: ${path}` : ""} — ${message}. ` +
        `This session's protocol is rendered from the kit base (${DEFAULT_AGENT_DEFINITION_PATH}); ` +
        `fix the file, nothing else will.`,
      degraded: true,
    };
  }
}
