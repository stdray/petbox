// Shared project resolver for the global agent-wiring kit.
//
// One global registry (`~/.petbox/projects.json`) maps a filesystem prefix to a PetBox
// project + the env var that holds its API key. The Claude Code user hooks and the global
// opencode plugin both run in EVERY project on the machine, so they resolve the active
// project by the current working directory (longest-prefix match) and no-op cleanly when
// the cwd is not registered.
//
// Plain TS for native node type-stripping: no enum/namespace/parameter-properties, type-only
// imports, zero deps.

import { readFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { wireLog } from "./wire-log.ts";

// Class A vs Class Б (bug: wire-silent-failures-invisible): a MISSING registry/keys-store file
// is the overwhelmingly common case (any machine before its first `petbox-wire` run, or any
// project directory these global hooks run in that was simply never wired) — that is legitimate
// silence, not a breakage, and must never leave a trace. A PRESENT file that fails to parse is a
// different animal (disk corruption, a hand-edit gone wrong, a partial write) — the previous
// code folded both into one catch-and-return-empty, so "porcha реестра" and "project not
// registered" (card item 4) looked identical from every hook's behavior. `isEnoent` tells them
// apart; only the non-ENOENT branch calls wireLog.
function isEnoent(e: unknown): boolean {
  return typeof e === "object" && e !== null && (e as NodeJS.ErrnoException).code === "ENOENT";
}

const DEFAULT_BASE_URL = "https://petbox.3po.su";

// A registry entry may carry extra keys written by older kits (e.g. the removed `promptRag` gate) —
// they are simply ignored here and dropped the next time wire.ts upserts the entry.
export type RegistryEntry = {
  prefix: string;
  project: string;
  envVar: string;
  baseUrl?: string;
};

export type ResolvedProject = {
  project: string;
  apiKey: string;
  baseUrl: string;
  envVar: string;
};

export function registryPath(homeDir: string = homedir()): string {
  return join(homeDir, ".petbox", "projects.json");
}

// Cross-platform key store written by wire.ts: ~/.petbox/keys.json is a flat JSON map
// { "<ENV_VAR>": "<key>" }. Read as a fallback when the env var is not set in the process
// (so a machine wired via `npx petbox-wire` works without a user-scope env var). Never throws.
// `homeDir` is injectable (tests only; every real caller uses the default) so the Class A/Б
// split above is unit-testable without touching the real ~/.petbox.
function readKeyStore(envVar: string, homeDir: string = homedir()): string {
  const path = join(homeDir, ".petbox", "keys.json");
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (e) {
    if (!isEnoent(e)) {
      wireLog("registry", `keys.json at ${path} unreadable — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    }
    return "";
  }
  try {
    const parsed = JSON.parse(raw);
    const v = parsed && typeof parsed === "object" ? parsed[envVar] : undefined;
    return typeof v === "string" ? v : "";
  } catch (e) {
    // File exists but is not valid JSON — corruption, not "no keys written yet" (Class Б).
    wireLog("registry", `keys.json at ${path} is not valid JSON — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return "";
  }
}

// Normalize a path for prefix comparison: unify separators to "/", drop a trailing
// separator, and lowercase on Windows (case-insensitive filesystem).
function normalize(p: string): string {
  let n = String(p).replace(/[\\/]+/g, "/");
  if (n.length > 1 && n.endsWith("/")) n = n.slice(0, -1);
  if (process.platform === "win32") n = n.toLowerCase();
  return n;
}

// Segment-boundary prefix match: "d:/my/prj/yoba" must NOT match "d:/my/prj/yobapub".
// dir is a prefix of, or equal to, the entry path (so worktree subfolders are covered).
function isUnderPrefix(dir: string, prefix: string): boolean {
  if (dir === prefix) return true;
  return dir.startsWith(prefix + "/");
}

// `homeDir` is injectable (tests only) — see readKeyStore's comment above.
export function readRegistry(homeDir: string = homedir()): RegistryEntry[] {
  const path = registryPath(homeDir);
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (e) {
    // Class A: no registry file yet — every unwired machine/project hits this constantly.
    if (!isEnoent(e)) {
      wireLog("registry", `projects.json at ${path} unreadable — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    }
    return [];
  }
  try {
    const parsed = JSON.parse(raw);
    const entries = parsed && Array.isArray(parsed.entries) ? parsed.entries : [];
    return entries.filter(
      (e: unknown): e is RegistryEntry =>
        !!e &&
        typeof (e as RegistryEntry).prefix === "string" &&
        typeof (e as RegistryEntry).project === "string" &&
        typeof (e as RegistryEntry).envVar === "string",
    );
  } catch (e) {
    // File exists but is not valid JSON — this IS the "порча реестра" the card calls out
    // (item 4): distinct from "project not registered", which is Class A and never gets here.
    wireLog("registry", `projects.json at ${path} is not valid JSON — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return [];
  }
}

// Resolve the active project for a directory. Returns null on ANY failure
// (no registry file, no match, empty env var) — never throws, because the hooks
// that call this run globally and must be a no-op outside registered projects.
// readRegistry/readKeyStore already classify their own failures (Class A silent vs Class Б
// traced); this outer catch only guards against a genuinely unexpected bug in the match logic
// below (e.g. a malformed dir argument) — if that ever fires, it is unambiguously Class Б.
// `homeDir` is injectable (tests only; every real caller uses the default homedir()).
export function resolveProject(dir: string, homeDir: string = homedir()): ResolvedProject | null {
  try {
    if (!dir || typeof dir !== "string") return null;
    const entries = readRegistry(homeDir);
    if (entries.length === 0) return null;

    const nd = normalize(dir);
    let best: RegistryEntry | null = null;
    let bestLen = -1;
    for (const e of entries) {
      const np = normalize(e.prefix);
      if (isUnderPrefix(nd, np) && np.length > bestLen) {
        best = e;
        bestLen = np.length;
      }
    }
    if (!best) return null;

    // env var wins; fall back to ~/.petbox/keys.json (the wire.ts key store).
    const apiKey = process.env[best.envVar] || readKeyStore(best.envVar, homeDir);
    if (!apiKey || apiKey.trim().length === 0) return null;

    const baseUrl = (best.baseUrl && best.baseUrl.trim()) || DEFAULT_BASE_URL;
    return {
      project: best.project,
      apiKey,
      baseUrl: baseUrl.replace(/\/+$/, ""),
      envVar: best.envVar,
    };
  } catch (e) {
    wireLog("registry", `resolveProject(dir=${dir}) unexpected failure — ${e instanceof Error ? e.message : String(e)}`, homeDir);
    return null;
  }
}
