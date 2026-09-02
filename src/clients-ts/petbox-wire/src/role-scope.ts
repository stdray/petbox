// WHERE role artifacts are rendered — the project tree, or the user's harness profile — and the
// machine-wide policy file that remembers the choice (card:
// normalize-all-environments-to-default).
//
// The problem this closes. Roles used to be rendered ONLY into each project tree: 6 projects x 5
// roles x 3 harness surfaces = 90 files that drift against each other generation by generation,
// with nothing checking them. The identical five roles do not differ per project — they are a
// property of the MACHINE (the owner's role->model bindings live in ~/.petbox/roles.json, which
// is machine-scoped too). Rendering them once into each harness's own user profile makes 15
// files, all with the origin marker, all provably identical because there is only one copy.
//
// Skills are the opposite case and deliberately stay per-project (see skill-files.ts): their
// bodies carry {{PROJECT}}/{{WORKSPACE}} substitution, so eight projects genuinely are eight
// different files, and a harness profile has no project to substitute.
//
// User-scope directories, all three documented by their harnesses:
//   claude-code -> ~/.claude/agents
//   opencode    -> ~/.config/opencode/agents   (PLURAL; the singular `agent` is opencode legacy)
//   droid       -> ~/.factory/droids
// The opencode singular/plural split is the real trap here: the PROJECT layout is
// `.opencode/agent` (apply-artifacts.ts, unchanged), while the USER layout is `agents`. They are
// not the same string and must not be derived from each other.
//
// Plain TS for native node type-stripping: zero deps.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import type { HarnessId } from "./harness-capabilities.ts";
import { wireLog } from "./wire-log.ts";

/** Where `apply` renders ROLE artifacts. Skills are unaffected by this axis — always per-project. */
export type RoleScope = "project" | "user";

export const ROLE_SCOPES: readonly RoleScope[] = ["project", "user"];

export function isRoleScope(v: string): v is RoleScope {
  return v === "project" || v === "user";
}

/**
 * Agent-file directory for `harness` inside the USER's harness profile, relative to $HOME.
 * Deliberately a separate function from apply-artifacts.ts's agentFilesDir (the project layout):
 * two of the three differ, and opencode differs by a single character (`agents` vs `agent`) —
 * exactly the kind of near-miss a shared helper with a boolean would eventually get wrong.
 */
export function userAgentFilesDir(harness: HarnessId): string {
  switch (harness) {
    case "opencode":
      // opencode reads BOTH `~/.config/opencode/agent` (legacy, singular) and `agents`; the
      // plural is the current documented name and the only one we ever write.
      return ".config/opencode/agents";
    case "claude-code":
      return ".claude/agents";
    case "droid":
      return ".factory/droids";
  }
}

/** Absolute user-scope agent directory for `harness`. */
export function userAgentFilesRoot(harness: HarnessId, homeDir: string = homedir()): string {
  return join(homeDir, userAgentFilesDir(harness));
}

// ---- machine policy (~/.petbox/wire.json) ------------------------------------------------
//
// Why persist at all: `apply --all --roles=user` is not a one-off command, it is a DECISION about
// this machine. A plain `petbox-wire apply` from a hook, or the apply step of a full `wire` run,
// would otherwise silently re-render roles back into the project tree the very next time anything
// touched it — re-creating the 90 files this card exists to delete. The flag sets the policy; the
// absence of a flag READS it.

export type WireConfig = {
  /** Missing file / missing field reads as "project" — the pre-card behavior, unchanged. */
  readonly roleScope: RoleScope;
};

const DEFAULT_WIRE_CONFIG: WireConfig = { roleScope: "project" };

export function wireConfigPath(homeDir: string = homedir()): string {
  return join(homeDir, ".petbox", "wire.json");
}

/**
 * Read ~/.petbox/wire.json. A missing file is Class A (no policy set yet — the overwhelmingly
 * common case) and reads as the default silently; a PRESENT but unparsable file is Class Б and
 * leaves a wire.log trace before falling back (registry.ts's split, same reasoning). Never throws.
 */
export function loadWireConfig(homeDir: string = homedir()): WireConfig {
  const path = wireConfigPath(homeDir);
  if (!existsSync(path)) return DEFAULT_WIRE_CONFIG;
  try {
    const raw: unknown = JSON.parse(readFileSync(path, "utf8"));
    if (typeof raw !== "object" || raw === null) return DEFAULT_WIRE_CONFIG;
    const scope = (raw as Record<string, unknown>)["roleScope"];
    if (typeof scope === "string" && isRoleScope(scope)) return { roleScope: scope };
    return DEFAULT_WIRE_CONFIG;
  } catch (e) {
    wireLog(
      "wire-config",
      `wire.json at ${path} exists but failed to parse — ${e instanceof Error ? e.message : String(e)}; ` +
        `falling back to roleScope=${DEFAULT_WIRE_CONFIG.roleScope}`,
      homeDir,
    );
    return DEFAULT_WIRE_CONFIG;
  }
}

/** Persist the policy (creates ~/.petbox if needed). Merges over whatever else the file holds. */
export function saveWireConfig(cfg: WireConfig, homeDir: string = homedir()): void {
  const path = wireConfigPath(homeDir);
  let existing: Record<string, unknown> = {};
  if (existsSync(path)) {
    try {
      const raw: unknown = JSON.parse(readFileSync(path, "utf8"));
      if (typeof raw === "object" && raw !== null) existing = raw as Record<string, unknown>;
    } catch {
      existing = {}; // unparsable — loadWireConfig already logged it; do not propagate the junk
    }
  }
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify({ ...existing, roleScope: cfg.roleScope }, null, 2) + "\n", "utf8");
}

/**
 * The scope THIS run uses: an explicit `--roles=<scope>` wins; otherwise the persisted machine
 * policy; otherwise "project". Returned alongside where it came from, because a run that silently
 * changed where it writes 15 files must be able to say why.
 */
export function resolveRoleScope(
  flag: RoleScope | undefined,
  homeDir: string = homedir(),
): { readonly scope: RoleScope; readonly source: "flag" | "config" | "default" } {
  if (flag !== undefined) return { scope: flag, source: "flag" };
  const cfg = loadWireConfig(homeDir);
  if (existsSync(wireConfigPath(homeDir))) return { scope: cfg.roleScope, source: "config" };
  return { scope: cfg.roleScope, source: "default" };
}
